# Acceptance evidence

The unchanged targets in `docs/implementation-plan.md` are the acceptance baseline. `docs/delivery-status.md` distinguishes implemented development functionality, verified gates, and external dependencies. `results/M00` through `M03` contain sanitized results and source fingerprints from the M0–M3 implementation.

Run `npm run check`, `npm run test:postgres` with a disposable TEST_DATABASE_URL, and `npm run test:e2e`. Firmware uses ESP-IDF5.4.2; see firmware/README.md. Never promote fixture tests to hardware/service acceptance. Tests with100 simultaneous claims are concurrency counts, not timing measurements. The20 continuity cases check canonical context assembly, not live model semantic accuracy. Browser axe checks complement and do not replace screen-reader/usability tests.

To close external gates, record board/client/AP conditions, provider/model/network timings (100 attempts including failures), approved identity registration, and five-participant task results. Do not save Wi-Fi passwords, factory setup secrets, provider keys, real repository content or personal conversation traces in these artifacts.
