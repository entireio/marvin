# Marvin — Controller

The app you set Marvin up with, drive it from, and watch it talk.

> This is not the project website — that lives in [`docs/`](../docs).

Three panes:

| | |
| --- | --- |
| **Setup** | Wi-Fi, the backend address, and the robot's credential — written over Bluetooth, once. |
| **Drive** | The command terminal, over Bluetooth. |
| **Voice** | Which AI service is in use, which robots are connected, and the conversation as it happens. Also where you start one. |

---

## Running it

Normally you do not run it at all: the backend serves it at `/app/`, behind the
same GitHub sign-in as the website, and that is where it can mint a device token
and show you a live conversation.

On a bench that is `./run_local.sh` from the repository root, and the controller
is at <http://localhost:8080/app/>. **Open it at `localhost`, not at your LAN
address** — Web Bluetooth only runs in a secure context, and Scan fails
anywhere else. The setup form knows about this and offers the robot your LAN
address rather than the one in the browser's address bar.

For working on the interface itself:

```bash
./serve.sh
```

Then open <http://localhost:3000>. Setup and Drive work fully — they are
Bluetooth, and owe the server nothing. Voice shows that the backend is
unreachable and the token button is disabled, which is the intended fallback
rather than a bug.

### Why you cannot just open `index.html`

Web Bluetooth is only available in a **secure context** — `https://` or
`localhost`. Opening the file directly with `file://` leaves
`navigator.bluetooth` undefined and the Scan button will fail.

### Browser support

| Browser | Works |
| --- | --- |
| Chrome (desktop, Android) | ✅ |
| Edge | ✅ |
| Opera | ✅ |
| Firefox | ❌ — no Web Bluetooth |
| Safari (macOS, iOS) | ❌ — no Web Bluetooth |

On Linux you may also need to enable
`chrome://flags/#enable-experimental-web-platform-features`.

---

## Files

```
index.html   Markup: the three panes and the command cheat sheet
style.css    All styling
app.js       BleConnection, Backend, Terminal, and the UI that wires them
serve.sh     Local static server on port 3000
```

No build step, no framework, no dependencies — deliberately. The whole app is
three static files that can be dropped on any host, which matters for something
meant to be reproduced by strangers.

---

## Architecture

`app.js` is four classes with one rule between them: **the UI layer never
touches `navigator.bluetooth` or `fetch` directly.**

**`BleConnection`** owns everything touching Web Bluetooth. `scan()`,
`disconnect()`, `send()`, `sendProvision()`, and an `on(event, cb)` subscription
for `data`, `connected` and `disconnected`. It knows nothing about the DOM.
Keeping that boundary intact is what would allow the transport to be swapped
(Web Serial, a WebSocket bridge) without touching the interface.

**`Backend`** owns everything touching the server: the status and token
endpoints, and the live-update socket. Every call fails cleanly when the page is
served from somewhere other than the backend.

**`Terminal`** is the output pane, the input field and the command history.

**`App`** subscribes to all three and renders.

### Two characteristics, not one

```js
const NUS_RX_UUID        = '6e400002-…';  // commands
const NUS_PROVISION_UUID = '6e400004-…';  // Wi-Fi, URL, token
```

Provisioning is separate because it requires an **encrypted** link. Anyone with
a radio can read an unencrypted BLE write from the next room, and a Wi-Fi
password is exactly the thing not to send that way. Writing to a characteristic
the firmware declared `WRITE_ENC` is also what makes the browser and the
operating system pair in the first place — so the requirement creates the
encryption rather than merely checking for it.

That is why the browser asks to pair the first time you press **Save to robot**,
and not when you connect.

The UUIDs must match [`firmware/src/ble_serial.h`](../firmware/src/ble_serial.h).
Web Bluetooth requires them in lowercase; the firmware uses uppercase. Both are
valid — do not "fix" one to match the other.

### What is never shown

The device token is minted by the server and written straight to the robot. It
is not displayed, not logged and not put in a field anyone could copy: it is a
bearer credential for a live microphone. The Wi-Fi password field is cleared as
soon as it has been sent, and the robot's own status readout reports only
whether a token is present.

---

## Setting a robot up

1. **Scan** and choose your robot.
2. **Setup → Wi-Fi.** Opening the pane asks Marvin to scan, and the dropdown
   fills with what *the robot* can see — which is not always what your laptop
   can see, and is the point of doing it this way. Your computer asks to pair at
   this moment: that is the encrypted link being established.

   Pick a network, type the password, press **Connect Marvin to Wi-Fi**. The
   chip beside the heading tracks it live — `connecting…`, then `connected` with
   the address, or `failed` with a reason worth acting on. A wrong password says
   so rather than timing out silently.

   2.4 GHz only: the ESP32 has no 5 GHz radio, so a 5 GHz-only network never
   appears in the list. Hidden networks do not broadcast a name, so pick
   **Other** and type it.
3. **Setup → The backend.** Wi-Fi got Marvin onto your network; this says *which
   server to talk to* and gives it a password for doing so. The address is
   pre-filled with the server serving this page. Pressing the button writes
   three things over Bluetooth: the device name, the address, and a token this
   server signs.

   The button is disabled unless a backend actually answers — the token can only
   come from one, and a button that fails with a 404 is worse than a button that
   explains itself.
4. Power-cycle Marvin.
5. **Ask the robot how it is doing.** You want `wifi: connected` and
   `cloud: connected`.
6. Go to **Voice** and press **Start listening** beside the robot. Say
   something, then press **Stop**.

   On a board that can hear its own name, "Hey Marvin" does the same thing. The
   C3 cannot, so the button is the way in there — and it is useful on the S3
   too, for when you are not in the room.

   **Stop** is worth using rather than waiting. It tells the AI service you have
   finished speaking, so the reply starts straight away instead of after the
   service decides the silence has gone on long enough.

If step 5 shows `cloud: connecting` forever, the usual causes are a wrong
address, a token minted against a different `SESSION_SECRET`, or a certificate
the robot does not trust — it verifies TLS against the roots in
[`firmware/src/certs.h`](../firmware/src/certs.h).

---

## Commands

The full protocol is documented in the root [`README.md`](../README.md#control-protocol).
The quick-command buttons are declared in `index.html` via `data-cmd`
attributes, so adding one is a single line of markup:

```html
<button class="btn btn-quick" data-cmd="T30">Look up</button>
```

If you add a command to the firmware, update the cheat sheet in `index.html`
too — see the checklist in [`CONTRIBUTING.md`](../CONTRIBUTING.md).

---

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| Scan button does nothing | Not a secure context — use `localhost`, not `file://` |
| "Web Bluetooth is not supported" | Firefox or Safari; switch to Chrome or Edge |
| `Marvin` not in the device picker | Robot not powered, or already connected to another client — BLE allows one at a time |
| Setup buttons stay disabled | The robot is on firmware without the provisioning characteristic — reflash it |
| The backend button stays disabled | Nothing answered at this origin. If the address bar says port 3000 you are on the static dev server; open `http://localhost:8080/app/` |
| The network list is empty | Marvin found nothing. 5 GHz-only networks never appear; press **Rescan** |
| Connects then immediately drops | Low battery; the ESP32 browns out when the motors draw current |
| Voice pane says the backend is unreachable | The page is not being served by the backend — open it at `/app/` |
| "Start listening" says the robot is not connected | The robot is off, or has not reached the backend. Check `?` over Bluetooth |
| No robots listed under Voice | Same — the list shows robots connected to the *backend*, not over Bluetooth |
| "Signed out" on every action | The session expired; reload the page |
