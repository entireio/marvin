# Tests to coordinate after the overnight work

Keep sound disabled until the user has returned. The physical board's eight-hour soak is expected to finish around09:40 America/Los_Angeles on September13. Opening serial, resetting or flashing before completion invalidates that uninterrupted run. Its completion is separate from the24-hour simulator run, expected around14:55.

While the board soaks, useful user-assisted work includes portal keyboard/screen-reader review, supported-browser identification, and configuring separate provider credentials for the M10 live matrix if desired. Do not paste keys in chat. The physical board is standalone: no motor, display or sensor hardware is available for acceptance.

After the soak, coordinate these tests in order:

1. **Quiet audio and recognition.** Install the reviewed audio test profile, explicitly agree on a quiet volume and confirm speaker audibility. Test both microphone channels, a spoken request, persisted transcript, response, stop and reconnect. Digital sample counts do not substitute for this listening check.
2. **Wake, echo and interruption.** Record microphone/speaker placement and noise conditions. Use the current development wake phrase (“Hi, ESP”), clearly distinct from a trained Marvin wake word. Measure actual speech onset, VAD detection, first audible response and playback stop across the original plan's required observations. Pre-roll and acoustic echo cancellation still need acceptance.
3. **Browser provisioning and Wi-Fi change.** Use a supported Web Bluetooth browser. Confirm robot-sourced network lists, provision a different reachable2.4 GHz AP, retain the same owner, and exercise recoverable wrong-password/disconnection/power-loss cases. Keep AP credentials out of saved evidence. Unlink/reset tests require a planned re-enrollment path.
4. **Signed update recovery.** Before any trial, verify backups, retain a usable fallback application, create dedicated release trust and review the isolated rollback-enabled image. Test valid update, wrong key/image, interrupted transfer, trial-health failure, and power cuts at persistence boundaries. Ten physical recovery trials are required; host fixtures do not meet this gate. Initial installation and the runtime coordinator remain under review.

Broader release gates also need representative participants, independent installation trials, live second-provider/cloud parity, approved Entire identity integration and the seven-day beta. They cannot be completed by a single unattended bench session.
