# Marvin — website server

A small Go server that puts a GitHub sign-in in front of the static site in
[`docs/`](../docs). It exists to solve one problem, stated at the bottom of
[`docs/README.md`](../docs/README.md): a GitHub Pages site is readable by anyone
who has the URL, even from a private repository, and restricting that needs
GitHub Enterprise Cloud. This serves the same files to the same standard, but
only to people you name.

It is built for Cloud Run — it listens on `$PORT`, keeps no server-side state,
and shuts down cleanly on `SIGTERM` — but it is an ordinary binary and will run
anywhere.

```
server/
├── main.go       Configuration, routing, static file serving
├── auth.go       The GitHub OAuth 2.0 round trip
├── session.go    Signed cookies, CSRF state, redirect safety
├── pages.go      The sign-in and error pages
└── *_test.go     Tests, including some against the real docs/ tree
```

Deployment is [`../deploy_google_cloud.sh`](../deploy_google_cloud.sh). Run it
with `--help` for the full list of settings.

---

## How a sign-in works

1. A request without a valid session cookie is redirected to `/signin`, which
   carries the page the visitor was after in a `return` parameter.
2. `/auth/login` mints a signed `state` — a nonce plus that return path — sets
   the nonce in a cookie, and redirects to GitHub.
3. GitHub sends the visitor back to `/auth/callback`. The signed `state` proves
   this server issued it; matching it against the cookie proves the browser
   finishing the flow is the one that started it. Both checks are needed —
   the signature alone would allow a replayed state, the cookie alone would
   allow a forged one.
4. The one-time code is exchanged for an access token, which is used once to
   read the visitor's login, and then discarded. Nothing is stored.
5. If that login is allowed, a signed session cookie is set and the visitor goes
   back where they were going.

Sessions are stateless signed cookies rather than server-side records, because
Cloud Run scales to zero and runs several instances at once — there is nowhere
to keep a session table that all of them would agree on.

---

## Configuration

Everything comes from the environment. Missing or unusable settings stop the
server at startup with a message naming what is wrong, rather than at the first
sign-in attempt.

| Variable | Required | Meaning |
| --- | --- | --- |
| `GITHUB_CLIENT_ID` | yes | Client ID of the GitHub OAuth App |
| `GITHUB_CLIENT_SECRET` | yes | Its client secret |
| `SESSION_SECRET` | yes | Cookie signing key, 32 characters or more |
| `ALLOWED_GITHUB_USERS` | see below | Comma-separated GitHub logins |
| `ALLOWED_GITHUB_ORGS` | see below | Comma-separated org logins; members may read |
| `ALLOW_ANY_GITHUB_USER` | see below | `true` admits every GitHub account |
| `DOCS_DIR` | no | Site directory. Default `docs`, `/srv/docs` in the image |
| `PORT` | no | Default `8080`; Cloud Run sets this |
| `BASE_URL` | no | Public origin. Only needed behind a custom domain |
| `SESSION_TTL_HOURS` | no | How long a sign-in lasts. Default `12` |

**At least one access rule is required.** With none set the server refuses to
start. This is deliberate: anyone can create a GitHub account in a minute, so
defaulting to "any signed-in user" would produce a public site wearing a login
page — the exact outcome this server exists to prevent, and one that would look
like it was working. `ALLOW_ANY_GITHUB_USER=true` is available when that really
is what you want, and has to be typed out.

`SESSION_SECRET` is likewise never generated on the fly. A per-instance key
would sign everyone out on each cold start and disagree between instances,
which presents as an intermittently flaky login rather than as the
configuration error it is.

---

## Running it locally

You need a GitHub OAuth App whose callback URL is
`http://localhost:8080/auth/callback` — a second, throwaway app is easier than
sharing one with production, since an OAuth App has a single callback URL.

```bash
export GITHUB_CLIENT_ID=Iv1.yourclientid
export GITHUB_CLIENT_SECRET=your-client-secret
export SESSION_SECRET="$(openssl rand -base64 48)"
export ALLOWED_GITHUB_USERS=your-github-login
cd server && go run . 
```

`DOCS_DIR` defaults to `docs` relative to the working directory, so run it from
the repository root if you start it any other way. Cookies drop their `Secure`
flag over plain HTTP so that this works without a certificate.

---

## Tests

```bash
cd server && go test ./...
```

The suite covers the parts where a mistake is a security bug rather than a
visible fault: signature verification and tampering, session and state expiry,
the open-redirect surface on the post-login hop, the allowlist, the fail-closed
startup rule, and cookie flags. `site_test.go` runs the whole gate against the
real `docs/` tree — every page, the CSS, the JavaScript and the demo video —
and asserts that a signed-out request gets none of it.

---

## Notes

- **`--allow-unauthenticated` on Cloud Run is required, and is not a mistake.**
  It refers to Google's own IAM layer. With IAM auth on, Cloud Run would reject
  every request with a 403 before the container saw it, so nobody could reach
  the sign-in page in order to authenticate. This container is the gate.
- **There is no Content-Security-Policy.** The site's pages carry inline scripts
  (the pre-paint theme switch) and pull fonts from Google, so an honest policy
  would need `'unsafe-inline'` and buy little. Adding one is a change to make
  against the site, not the server.
- **The site is served verbatim**, `docs/README.md` and `DESIGN-BRIEF.md`
  included, so that what you review here is exactly what GitHub Pages would
  publish later.
- **Signing out cannot sign you out of GitHub**, only out of this site. The
  logout page says so.
