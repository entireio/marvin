# Marvin — Controller

A browser-based remote control for Marvin. It speaks Web Bluetooth directly to
the robot's Nordic UART Service — no server, no app store, no pairing utility.

> This is the app you **drive the robot with**. It is not the project website —
> that lives in [`docs/`](../docs).

---

## Running it

```bash
./serve.sh
```

Then open <http://localhost:3000>, click **Scan**, and choose `Marvin` from the
browser's device picker.

`serve.sh` just runs `npx -y serve -l 3000 .`; any static file server works
equally well.

### Why you cannot just open `index.html`

Web Bluetooth is only available in a **secure context** — `https://` or
`localhost`. Opening the file directly with `file://` leaves
`navigator.bluetooth` undefined and the Scan button will fail. Serving from
localhost is the reason `serve.sh` exists.

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
index.html   Markup, including the command cheat sheet
style.css    All styling
app.js       BleConnection class + terminal UI logic
serve.sh     Local static server on port 3000
```

No build step, no framework, no dependencies — deliberately. The whole app is
three static files that can be dropped on any host, which matters for something
meant to be reproduced by strangers.

---

## Architecture

`app.js` splits into two halves:

**`BleConnection`** owns everything touching `navigator.bluetooth`. It exposes
`scan()`, `disconnect()`, `send()`, and an `on(event, cb)` subscription for
`data`, `connected`, and `disconnected`. It knows nothing about the DOM.

**The UI layer** subscribes to those events and renders the terminal. It should
never call `navigator.bluetooth` directly — keeping the boundary intact is what
allows the transport to be swapped (Web Serial, WebSocket bridge) without
touching the interface.

The service UUIDs at the top of `app.js` must match
[`firmware/src/ble_serial.h`](../firmware/src/ble_serial.h). Web Bluetooth
requires UUIDs in lowercase; the firmware uses uppercase. Both are valid — do
not "fix" one to match the other.

---

## Commands

The full protocol is documented in the root [`README.md`](../README.md#control-protocol).
The quick-command buttons in the footer are declared in `index.html` via
`data-cmd` attributes, so adding one is a single line of markup:

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
| Connects then immediately drops | Low battery; the ESP32 browns out when the motors draw current |
| Garbled escape characters in output | Firmware bypassed `respond()` and wrote raw ANSI to BLE |
