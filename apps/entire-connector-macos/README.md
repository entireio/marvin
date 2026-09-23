# Marvin Entire Connector for macOS

This is the optional status-bar shell for the portable connector core. It has no Dock icon and provides pairing, connection status, pause/reconnect, and reconnect-at-launch preference. The connector core performs the protocol and all bounded Entire CLI reads.

The menu-bar item uses Entire's official small-size robot icon from its brand kit. AppKit treats it as a template image at 18 points, allowing macOS to provide the correct menu-bar contrast in light and dark appearances without modifying the mark.

Build a local `.app` bundle with:

```sh
./build-app.sh
```

Release builds must additionally be signed, notarized, distributed through a checksum-verifying updater, and tested against the minimum supported macOS version.
