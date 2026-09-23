# Compressed Pet microphone audio

Status: accepted for protocol 1.12

## Decision

Pet microphone frames use frame-independent IMA-ADPCM at 16 kHz mono. A 30 ms frame contains 480 PCM samples and is approximately one quarter the size of raw 16-bit PCM. Each frame carries its own predictor and index, so one dropped frame does not corrupt later audio. Protocol 1.12 negotiates the codec explicitly; the server continues to accept raw PCM from older firmware.

Control and audio remain on one authenticated WebSocket. A second TLS connection was rejected for the current board because live measurements under Wi-Fi and TLS showed only about 10–11 KB of free internal heap and a 4–5 KB largest block. The existing PSRAM capture queue remains bounded, and a microphone write timeout is measured and reported rather than deliberately closing the control link.

## Why not Opus yet

The ESP32-S3 has enough CPU and PSRAM for Opus in isolation, but the official Espressif codec figures include significant encoder heap and task-stack requirements. Those allocations do not fit the currently observed contiguous internal-memory margin with sufficient safety allowance for Wi-Fi, TLS, DMA, and reconnection. Opus may be reconsidered after a measured internal-memory budget leaves at least 64 KB free and a 40 KB contiguous block during a sustained voice/TLS stress test.

IMA-ADPCM has negligible state and CPU cost, a small auditable implementation, predictable fixed-size frames, and no additional runtime dependency. Its lower compression efficiency is acceptable at the current 16 kHz mono input rate.

## Operational safeguards

- 45-second application liveness tolerance absorbs temporary network stalls.
- Presence reports `reconnecting` for 30 seconds rather than immediately claiming the Pet is offline.
- Firmware reports RSSI, reset reason, internal heap, last link fault, codec timing, and audio write timeouts.
- The remote-control UI distinguishes offline and reconnecting from a genuine missing firmware capability.
- An offline wake produces a distinct local three-note cue instead of silent head motion.
