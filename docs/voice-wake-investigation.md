# Voice wake investigation — 14 September 2026

## Finding

The board was account-linked, online over authenticated WSS, and advertising voice capability. The backend had OpenAI voice configured. However, the physical USB status reported `wakeActivationDisabled: true`, and the backend initially reported zero voice starts and zero audio traffic.

The audible profile requests `CONFIG_MARVIN_WAKE_AUTOSTART=y`, but its existing generated sdkconfig had that option unset, and the corresponding compiled header omitted it. `body_audio.c` initializes wake activation to false without this option. The audible build helper reused the generated configuration, allowing a stale setting to override profile defaults.

## Immediate recovery and validation

Enabled activation using the board's `W` console command. Physical status then reported `wakeActivationEnabled: true` and `wakeActivationDisabled: false`.

A short computer-speaker stimulus, “Hey Marvin, what is up?”, triggered a local wake, followed by `voice: listening`. The backend recorded the transcript “Hey Marvin, what's up?” and a completed assistant reply beginning “Hey! Not much, I’m here…”. Its diagnostics recorded one voice session, 391,360 microphone bytes received and 508,800 response-audio bytes sent to the board. This verifies wake, provider processing and response transport; subjective speaker audibility was not independently confirmed.

One AFE feed fault appeared during the observed startup and did not increase in subsequent samples. It did not prevent this successful turn. It remains a separate startup observation, not the cause of disabled activation.

## Permanent correction

The audible build helper now removes only its generated sdkconfig before configuring, so checked-in profile defaults take effect on each build, matching the release helper's approach. The regenerated configuration enables wake autostart and retains the requested 8192-byte NimBLE host stack. The full ESP-IDF audible firmware build completed successfully; shell syntax and diff whitespace checks also passed.

Signed production builds now fail at compile time if wake autostart is absent, except for the explicit rollback-failure fixture. The guarded bootstrap installer also refuses a release build without wake autostart.

On 15 September, a verified double-read recovery snapshot was taken before installing the corrected bootloader and application in their existing regions. Runtime NVS, Wi-Fi, factory identity, OTA metadata and the wake model were preserved. A subsequent reset received no `W` command and reported `wakeActivationDisabled: false`, `update: boot_confirmed` and an authenticated online uplink. The protocol 1.3 welcome parser was corrected after the first verification boot exposed its stale 1.2 check.
