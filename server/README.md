# Marvin — backend

One Go binary, three jobs:

- **the robot's endpoint** — a TLS WebSocket carrying a conversation to and from
  an AI service
- **the web controller** — set Marvin up over Bluetooth, drive it, watch it talk
- **the project website** — the static site in [`docs/`](../docs), behind a
  GitHub sign-in

They are one process because the robot and the controller have to agree about
the same thing: which AI service is in use, which robots are connected, and what
token a given robot carries. Splitting them would mean a second deployment whose
only purpose was to tell the first one those three facts.

It is built for Cloud Run and for ECS Fargate — it listens on `$PORT`, keeps no
durable state, and shuts down cleanly on `SIGTERM` — but it is an ordinary
binary and will run anywhere.

```
server/
├── main.go        Configuration, routing, static file serving
├── auth.go        The GitHub OAuth 2.0 round trip
├── session.go     Signed cookies, CSRF state, redirect safety
├── voice.go       Provider selection, the controller API, device auth
├── pages.go       The sign-in and error pages
├── protocol/      The wire format the robot and this share
├── audio/         Resampling between 16 kHz and whatever the provider wants
├── provider/      OpenAI Realtime and Gemini Live, behind one interface
├── harness/       What happens to everything the AI says
├── device/        The robot's WebSocket endpoint and the live device list
├── internal/token/  Signed cookies and device credentials, one implementation
└── cmd/fakedevice/  A robot made of a WAV file, for testing without hardware
```

---

## How a conversation works

```
  Marvin ──wss://…/v1/device──▶ device ──▶ provider ──▶ OpenAI Realtime
         ◀───────────────────── harness ◀──            or Gemini Live
```

1. The robot starts a turn and sends `wake`, having **nodded** first. Either it
   recognised "Hey Marvin", or somebody pressed *Start listening* in the
   controller — which is the only way in on a board that cannot listen for its
   own name, and arrives as an `act` message carrying `W1`. Either way the nod
   happens locally, on the robot's own evidence rather than on a reply from
   here, so it is immediate rather than a round trip late.
2. `device` dials the AI service and answers `listen`.
3. The robot streams 20 ms frames of 16 kHz mono PCM16. `provider` resamples to
   whatever the service wants and forwards them.
4. The service decides the person has stopped speaking — it is listening to the
   same audio and is better at it than a threshold on a microcontroller — and
   starts replying.
5. Every event from the service passes through `harness` before reaching the
   robot. Today one handler forwards the audio and publishes transcripts.
6. `speak_begin` tells the robot to stop sending microphone audio. Its
   microphone and speaker are centimetres apart with no echo cancellation, so an
   open uplink would feed Marvin its own voice and the service's turn detection
   would read that as an interruption.
7. `speak_end`, and the robot is listening for its name again.

The provider session lives for one exchange. Holding it open between questions
would save a few hundred milliseconds of dialling and cost a billed, idle
connection for every robot that is merely switched on.

---

## Adding a capability

This is the part the architecture exists for. Detecting that someone asked for a
note to be taken, or told the robot to turn around, is **another handler**, not a
change to the audio path:

```go
type Handler interface {
    Name() string
    OnEvent(ctx context.Context, ev provider.Event, out Emitter) error
}
```

The detection itself is the model's own function calling. A handler implements
`ToolDeclarer` to say what it can do, the model calls the tool when the
conversation warrants it, and the handler acts — emitting an `act` frame to move
the robot, or writing the note. No second classifier, no keyword matching, and
the model keeps the context that makes "actually, make that tomorrow" work.

Actions reach the robot as `{"t":"act","cmd":"T40"}` or
`{"t":"act","gesture":"nod"}`. The first goes through the same command
interpreter the serial console and Bluetooth already use, so there is one
interpreter rather than two that drift.

`POST /api/device-action` is that same path, driven by a person instead of a
model — `Hub.Send` looks up the robot's live connection and writes to it. The
listening button in the controller is its only caller today.

---

## Adding a provider

One file in `provider/`, implementing two interfaces:

```go
type Provider interface {
    Name() string
    DefaultModel() string
    Dial(ctx context.Context, cfg Config) (Session, error)
}

type Session interface {
    SendAudio(pcm []byte) error   // 16 kHz mono PCM16, from the robot
    CommitTurn() error
    SendToolResult(id string, result any) error
    Events() <-chan Event
    Close() error
}
```

Each implementation does its own resampling, so everything above deals in the
robot's 16 kHz and never has to know that OpenAI wants 24 kHz both ways while
Gemini wants 16 in and 24 out. Neither needs an SDK: both services are JSON
events over a WebSocket.

---

## Routes

| Path | Who | |
| --- | --- | --- |
| `/healthz` | anyone | liveness |
| `/signin`, `/auth/*` | anyone | the sign-in flow itself |
| `/v1/device` | a robot | WebSocket, bearer token |
| `/api/status` | signed in | providers, robots, wake word |
| `/api/provider` | signed in | switch the AI service |
| `/api/device-token` | signed in | mint a robot's credential |
| `/api/device-action` | signed in | tell a connected robot to do something |
| `/ws/controller` | signed in | live updates |
| `/app/` | signed in | the web controller |
| everything else | signed in | the website |

The robot's endpoint answers **401**, not a redirect. It is not a browser, and a
redirect to a GitHub sign-in page would leave it retrying an HTML document
forever.

---

## Credentials

Both kinds of token are signed with `SESSION_SECRET`, by one implementation in
`internal/token`.

Nothing is stored. The server scales to zero and runs several instances at once,
so a table of sessions or devices would need a database this deployment does not
otherwise want; a signature answers the same question without one. The cost is
that revoking one robot means rotating the key, which signs everyone out and
takes every robot with it — the right trade for a household with a robot in it,
and the wrong one at the point where this has outgrown a handful of environment
variables.

A robot's token is minted in the controller and written to the robot over
Bluetooth, so it never travels the network in the clear and never has to be
typed. It lasts a year by default, because renewing it means getting the robot
back on a bench.

---

## Configuration

Everything comes from the environment. Missing or unusable settings stop the
server at startup with a message naming what is wrong, rather than at the first
sign-in attempt.

| Variable | Required | Meaning |
| --- | --- | --- |
| `GITHUB_CLIENT_ID` | yes | Client ID of the GitHub OAuth App |
| `GITHUB_CLIENT_SECRET` | yes | Its client secret |
| `SESSION_SECRET` | yes | Signing key, 32 characters or more |
| `ALLOWED_GITHUB_USERS` | see below | Comma-separated GitHub logins |
| `ALLOWED_GITHUB_ORGS` | see below | Comma-separated org logins; members may read |
| `ALLOW_ANY_GITHUB_USER` | see below | `true` admits every GitHub account |
| `OPENAI_API_KEY` | no | Enables OpenAI Realtime |
| `GEMINI_API_KEY` | no | Enables Gemini Live |
| `AI_PROVIDER` | no | `openai` or `gemini`. Defaults to whichever has a key |
| `AI_MODEL`, `AI_VOICE` | no | Override the provider's defaults |
| `AI_INSTRUCTIONS` | no | Marvin's system prompt. Default asks for short answers |
| `DEVICE_TOKEN_TTL_DAYS` | no | Default `365` |
| `DOCS_DIR` | no | Site directory. Default `docs`, `/srv/docs` in the image |
| `CONTROLLER_DIR` | no | Default `controller`, `/srv/controller` in the image |
| `PORT` | no | Default `8080` |
| `BASE_URL` | no | Public origin. Needed behind a custom domain or an ALB |
| `SESSION_TTL_HOURS` | no | How long a sign-in lasts. Default `12` |
| `MARVIN_LOCAL` | no | `true` runs without a sign-in, for a bench. See below |

**At least one access rule is required.** With none set the server refuses to
start. This is deliberate: anyone can create a GitHub account in a minute, so
defaulting to "any signed-in user" would produce a public site wearing a login
page — the exact outcome this gate exists to prevent, and one that would look
like it was working. `ALLOW_ANY_GITHUB_USER=true` is available when that really
is what you want, and has to be typed out.

**No AI key is required.** The site and the controller work without one; the
robot simply cannot hold a conversation, and says so rather than failing
silently.

`SESSION_SECRET` is never generated on the fly. A per-instance key would sign
everyone out on each cold start, disagree between instances, and invalidate
every robot's token — which presents as an intermittently flaky login rather
than as the configuration error it is.

### The provider chosen in the controller is per-instance

`AI_PROVIDER` is the durable setting. Switching in the web controller changes it
in memory, on the instance that served the request. Both deploy scripts pin the
service to a single instance so that a switch made there is the switch every
robot sees; raising that means the controller's view can fragment.

---

## Running it on a bench

```bash
./run_local.sh                      # from the repository root
```

The whole system on one machine, with the robot on the same Wi-Fi. No cloud
account, no GitHub OAuth app, no certificate — which matters, because otherwise
finding out whether a microphone is wired the right way round would mean
registering an OAuth application first.

What `MARVIN_LOCAL=true` changes:

- **The sign-in is off.** `GITHUB_CLIENT_ID` and `GITHUB_CLIENT_SECRET` are not
  required, and no access rule is needed.
- **Only private addresses are served.** Something has to hold the line once the
  sign-in is gone, so `localOnly` in `local.go` refuses any request whose `Host`
  is a public name or address — 403, before anything else runs. That defends
  against a port forward somebody left open, not against the person sitting next
  to the robot.
- **The robot's endpoint is unchanged.** It still demands a valid device token.
  Dropping the sign-in must not open a microphone to everyone on the café Wi-Fi,
  and `TestLocalModeStillRequiresADeviceToken` is there to keep it that way.

`SESSION_SECRET` is still required, and `run_local.sh` keeps one in
`.marvin-local-secret` (gitignored) rather than generating a fresh one each
time — the same key signs the robots' credentials, so regenerating it would
mean re-provisioning the robot on every restart.

Neither deploy script sets `MARVIN_LOCAL`, so it cannot travel to a cloud by
accident.

### Two addresses, and why

The controller has to be opened at **`http://localhost:PORT/app/`**. Web
Bluetooth only runs in a secure context — HTTPS or localhost — so Scan fails
if you open it at your LAN address. That is a browser rule.

But `localhost` is the one address the robot certainly cannot reach. So
`/api/status` reports a `device_url` built from this machine's LAN address
rather than from the browser's, and the setup form uses it. Without that, the
form would cheerfully write `localhost` into the robot and the only symptom
would be a connection that never succeeds.

The LAN address is found by asking the routing table which source address it
would use to reach the internet — no packets are sent, and it picks correctly on
a machine with several interfaces. On an isolated bench network with no route
out, it falls back to the first private address on an interface that is up.

---

## Running it locally with the sign-in

If you want to exercise the real GitHub flow, you need an OAuth App whose
callback URL is
`http://localhost:8080/auth/callback` — a second, throwaway app is easier than
sharing one with production, since an OAuth App has a single callback URL.

```bash
export GITHUB_CLIENT_ID=Iv1.yourclientid
export GITHUB_CLIENT_SECRET=your-client-secret
export SESSION_SECRET="$(openssl rand -base64 48)"
export ALLOWED_GITHUB_USERS=your-github-login
export OPENAI_API_KEY=sk-...        # optional
cd server && go run .
```

`DOCS_DIR` and `CONTROLLER_DIR` default to `docs` and `controller` relative to
the working directory, so run it from the repository root if you start it any
other way. Cookies drop their `Secure` flag over plain HTTP so this works
without a certificate.

### Testing without a robot

```bash
cd server
go run ./cmd/fakedevice -key "$SESSION_SECRET" -in question.wav -out answer.wav
```

`fakedevice` connects exactly as the robot does, streams a WAV file as though it
were the microphone — paced at 20 ms a frame, because the services use silence
to decide the speaker has finished — and writes the reply to another WAV. It
exercises the device link, the provider, the harness and the resampling. The
difference between debugging this in seconds and debugging it by walking to a
shelf and power-cycling something.

Any rate and any channel count will do for the input; it is converted on the way
in.

---

## Tests

```bash
cd server && go test ./...
```

The suite covers the parts where a mistake is a security bug rather than a
visible fault: signature verification and tampering, session and device-token
expiry, the open-redirect surface on the post-login hop, the allowlist, the
fail-closed startup rule, cookie flags, and that the controller and its API did
not open a way past the sign-in. `site_test.go` runs the whole gate against the
real `docs/` tree and asserts that a signed-out request gets none of it.

`device/device_test.go` runs a whole conversation against a stub AI service:
hello, wake, streamed audio, a reply, and the robot released at the end. It is
what caught the reply's final audio frame being sent *after* `speak_end`, which
on real hardware clips the last few milliseconds of every sentence.

`audio/resample_test.go` asserts that resampling a stream in 20 ms chunks gives
bit-identical output to resampling it whole. Filter history that is not carried
across chunk boundaries is not a test failure in production — it is a click,
fifty times a second.

---

## Notes

- **`--allow-unauthenticated` on Cloud Run is required, and is not a mistake.**
  It refers to Google's own IAM layer. With IAM auth on, Cloud Run would reject
  every request with a 403 before the container saw it, so nobody could reach
  the sign-in page in order to authenticate. This container is the gate.
- **Not AWS App Runner.** It is the obvious choice for a container like this and
  it cannot carry a WebSocket. `deploy_aws.sh` uses ECS Fargate behind an ALB.
- **There is no Content-Security-Policy.** The site's pages carry inline scripts
  (the pre-paint theme switch) and pull fonts from Google, so an honest policy
  would need `'unsafe-inline'` and buy little.
- **Audio leaves the room** whenever a turn is open, and is sent to whichever AI
  service is configured. Nothing is recorded here — audio is forwarded frame by
  frame and never written to disk — but it does go somewhere.
