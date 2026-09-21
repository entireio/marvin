# M5 — Realtime web voice

13 September 2026. **Implementation and scoped live alpha checks pass; the original M5 release gate remains open.** The funded OpenAI key and Entire CLI work. Live text, backend voice, browser capture/playback, repository voice and narrow cross-surface recall checks pass. A controlled100-sample provider/acoustic latency baseline remains open.

## Use voice

Set the following server environment values and restart Marvin:

```dotenv
MODEL_PROVIDER=openai
OPENAI_MODEL=gpt-5.4-mini-2026-03-17
VOICE_PROVIDER=openai
OPENAI_REALTIME_MODEL=gpt-realtime-2.1
OPENAI_TRANSCRIPTION_MODEL=gpt-4o-mini-transcribe
OPENAI_VOICE=marin
```

Set `OPENAI_API_KEY` privately in `.env`. Use a project with API credit and access to these models. The text snapshot is a fast coding/tool-use candidate; the realtime model supports speech and function calls. Their suitability and latency still need live measurement. They are explicit choices, not promises of account availability. [Text model](https://developers.openai.com/api/docs/models/gpt-5.4-mini), [voice model](https://developers.openai.com/api/docs/models/gpt-realtime-2.1).

Open **Talk to Marvin**, then **Start voice**. Opening the panel alone does not request the microphone. Allow microphone access when prompted. Speak naturally, interrupt by speaking or choose **Stop speaking**, and use **Mute** when needed. **Continue in text**, Escape, closing the dialog, network failure, or device removal stop capture and playback. Permission denial gives browser recovery instructions. Reconnect explicitly to resume after an error or idle close. The same conversation and repository selection remain in place.

The voice is disclosed as AI-generated. Raw audio passes through the Marvin server to OpenAI while voice is active; Marvin persists transcripts and repository evidence, not audio. Provider data handling is governed separately by the provider account. Use HTTPS outside localhost. The tested transport is mono PCM s16le at 24 kHz. Browsers without the required capture/audio-worklet support display an actionable error.

## Architecture and limits

A dedicated authenticated WebSocket carries versioned Marvin controls and bounded PCM frames. It accepts only web voice; the server generates an independent route ID. Origin, CSRF and session checks guard setup. Input and output are rechecked against the login session. One voice session per owner and at most 32 sessions per process are allowed. Voice audio goes only to its initiating socket and is absent from persisted event replay and general event broadcasts.

The runtime uses its existing turn leases, cancellation, canonical history, repository adapter and tool policy. Spoken transcripts become ordinary durable `web_voice` turns. Each model response has an explicit context rebuilt from saved history and the current utterance. A provider session is replaceable. Interrupted assistant text remains marked partial; it does not prove every generated word was heard.

The OpenAI adapter uses server VAD with automatic responses disabled, so Marvin controls when a durable turn starts. It waits for the input transcript, then requests speech using the original audio item and saved context. This adds transcription latency, which must be included in acceptance. Tool results remain bounded untrusted evidence, with no physical or repository-write tools. SDK 7.15 omits the documented `item_reference` input type, so the adapter uses a narrowly scoped type assertion for that wire field; a real SDK/TLS transport test checks the request. [Custom context and VAD controls](https://developers.openai.com/api/docs/guides/realtime-conversations), [server WebSocket transport](https://developers.openai.com/api/docs/guides/voice-websockets).

Capture runs in an AudioWorklet with 20 ms frames. Browser echo cancellation and noise suppression are requested. A local energy detector flushes scheduled playback immediately without waiting for the network; server VAD also cancels the response. This detector needs acoustic tuning and is not claimed to pass noisy-room tests. Muting disables the input track and forwarding. Playback queues, provider events, packet rate and pending work are bounded. Idle voice closes after 90 seconds; a session retires after 15 minutes; provider resources close immediately on exit (zero grace). No automatic microphone restart occurs.

The deployment remains one API process. This is not a distributed voice broker, and production deployment/operations remain later milestones.

## Verification

Automated tests use explicit recorded provider replies and synthetic browser audio, never hidden fake live providers. They cover 20 text→voice→text reconstruction journeys with fresh voice connections, 100 routed audio interactions with another browser and a linked simulated robot, revoked login, permission refusal, microphone loss, socket loss, mute, cancellation fencing, idle cleanup, malformed input and tool authorization. The actual OpenAI SDK runs against a local TLS server using a generated trusted test certificate; TLS verification stays enabled.

**Current regression:155 tests, typechecking and production build passed;14 fixture browser tests passed earlier.** Live evidence includes20 reviewed text–voice–text recall journeys with source-surface metadata,100 browser voice rounds (zero failures; input-end to scheduled audio p95=2,040 ms, including VAD), and100 synthetic local interruption trials (p95=60 ms). These timing tests use headless Chrome152 on macOS and do not measure physical acoustics. Historical evidence remains in `tests/acceptance/results/M05/results.json`; current runs have separate descriptive JSON files.

Run `npm run check` and `npm run test:e2e`. The fixture browser suite ran in the official Playwright1.63 Linux container. With full system access, the live measurement scripts use installed Chrome on macOS. Voice browser tests reuse a signed-in test session to respect the application's real login rate limit.

For a small live check, prepare consented or synthetic **mono, 24 kHz, signed 16-bit little-endian PCM**, then run:

```sh
npm run smoke:voice -- --input /absolute/path/to/test-input.pcm
```

This invokes the live provider through the real Marvin backend in an isolated in-memory database. It records transcript persistence, output byte count, sanitized failures and time from detected speech end to received audio. It never saves raw input/output in acceptance artifacts. It is not browser/acoustic latency acceptance. Run `npm run smoke:provider` for a text smoke request. Both consume provider credit when successful.

## Open acceptance gates

M5 implementation is complete. The earlier billing failures are resolved. Live backend voice returned a saved transcript and122,400 audio bytes, with1,262 ms from detected end to first received audio in its initial smoke test.

Twenty narrow live recall journeys and a grounded repository voice query against `spedemon/marvin` pass. M4's live Entire read smoke and20-question repository review also pass. A post-phase release-certification follow-up is at least100 live-provider/acoustic latency observations under a declared controlled baseline, reporting every failure; the target remains first audible response p95≤2seconds and local playback stop p95≤200milliseconds, with VAD delay separated. The existing100 browser rounds measure synthetic input end to scheduled output at p95=2,040ms and therefore do not satisfy that acoustic/provider measurement.
