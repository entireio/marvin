# Marvin Entire Connector

The connector is the fallback for deployments that cannot keep an Entire login in Marvin's cloud environment. It runs on the user's computer, uses the already authenticated local Entire CLI, and makes an outbound WebSocket connection to Marvin. It never accepts shell commands; the protocol exposes only the repository operations already allowed by Marvin.

Build it with:

```sh
go build -o marvin-entire-connector .
```

In Marvin, open **Settings → Connections → Use this Mac instead** and create a one-time pairing code. Then run the displayed command. The connector saves its revocable Marvin credential with user-only filesystem permissions and reconnects without reusing the pairing code.

The same core builds on macOS, Linux, and Windows. The macOS status-bar project in `../entire-connector-macos` is a small UI shell around this executable.
