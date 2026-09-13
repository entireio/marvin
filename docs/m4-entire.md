# M4 — Entire repository integration

12 September 2026. **Local implementation delivered; M4 acceptance remains open.** The production multi-user integration requires an approved Entire application/API contract. [Architecture decision](decisions/0004-entire-integration.md).

## Local setup

In the terminal where `entire login` completed, run `entire auth status`. The browser's “Signed in” page confirms browser authorization; verify that the terminal also has a usable login. In this task, the installed CLI still reported “Not logged in” after the user confirmed browser success. A credential-store access difference is possible, but its cause has not been established. Do not copy tokens, cookies, or an old authorization URL into Marvin.

Then run:

```sh
cd /Users/stefanopedemonte/Projects/Marvin_software
npm run entire:setup -- --context us.auth.entire.io
```

Use your context from `entire auth contexts` if different. Setup lists up to 25 mirrored repositories, accepts an exact repository name, and optionally asks for a local checkout root. It verifies repository access before replacing the local binding file, then prints two paths to add to `.env`. Restart Marvin from that same terminal and choose **Settings → Connections → Connect Entire**. Setup needs local SQLite and development or local-passphrase authentication. It refuses hosted OIDC configuration. It does not install hooks or change Git remotes.

For non-interactive repository selection, use `--repo owner/repo --checkout /absolute/checkout`. Omit the checkout for indexed search only. Entire-native repositories use `--kind native --repo project/repo`. A checkout must be the repository root with a matching GitHub or `entire://…/gh/…` or `entire://…/et/…` origin. The adapter reads committed objects and does not fetch updates. Pull updates through your normal Git workflow.

`npm run entire:check` uses the configured binding to verify access and exercise bounded summary/search/history reads. It writes sanitized counts and status codes under `work/m4/live-smoke.json`. This is a smoke check, not semantic acceptance. `entire enable` installs capture hooks and is unnecessary for reading existing available data; a new repository may have no captured history until capture is separately enabled and work is recorded.

## What is implemented

The runtime depends on a replaceable `RepositoryIntegration` interface. The local adapter invokes allowlisted commands from Entire CLI 0.10.6 with an explicit saved context and configured repository scope. The UI supports repository selection, connection checks/disconnection, indexed code and history search, committed file excerpts, and stored checkpoint/session metadata. Chat can receive evidence cards through the same policy-controlled tools. Fixture-model mode explicitly does not claim AI interpretation of those results.

Every read verifies repository access. Cached source also requires a fresh access check; cache entries include owner, repository, commit, path and range. Disconnect invalidates local cache and prevents in-flight results from being delivered. It preserves conversations and robot configuration. Limits are four subprocesses per server, 20 seconds per command, bounded output, ten evidence items, 160 source lines, and a 32-entry source cache. These limits support a local process and are not a distributed scaling strategy.

Source evidence identifies the actual Git commit and line range. Indexed search without a revision says so; partial and empty results disclose limitations. Excerpts render as text, and repository text is treated as evidence rather than instructions. Sensitive filenames and selected credential patterns are excluded/redacted; this is not a guarantee that arbitrary source contains no secrets. Select repositories appropriate for your model provider because retrieved evidence used in chat can be sent to that provider.

The optional graph route is disabled by default. Its plugin response contract and live behavior have not been verified, so it is not an accepted M4 capability. Graph reads reject a checkout revision change during retrieval. Hosted OIDC configuration cannot enable this CLI adapter. Marvin must not reuse Entire's `entire-cli` OAuth client or browser sessions as portal authentication.

## Measured and pending gates

- Current automated suite:130 tests passed, including model routing and the graph consistency regression. Tests include real temporary Git objects plus explicit recorded Entire responses; they are not live service tests.
- Eight browser tests passed with no serious/critical accessibility findings at audited states, including safe evidence rendering, pagination and recoverable errors. Final rerun results are recorded alongside the implementation evidence.
- Live authorized repository: `spedemon/marvin`; CLI summary/search/history and committed-source reads pass with the current login.
- Twenty golden questions:20/20 manually reviewed correct in `live-repository-model-review.json`, using `gpt-5.4-2026-03-05` and the recorded committed revision. Two earlier Mini runs scored19/20 with different missed traces; those failures remain recorded. This is a narrow single-repository gate, not general quality certification.
- Real checkpoint/session coverage, graph capability, service expiry/revocation/rate-limit behavior: not accepted from fixtures.
- Hosted integration: pending approved external authentication and read API contracts, per-user credential lifecycle, tenant isolation and deployment/load verification.

The [original milestone plan](implementation-plan.md) remains the acceptance baseline. M4 cannot be called complete while these gates remain open.

## Official references

- [CLI authentication](https://docs.entire.io/cli-reference/auth.md)
- [CLI search](https://docs.entire.io/guides/search/search-in-cli.md)
- [CLI API caveats](https://docs.entire.io/cli-reference/api.md)
- [Entire API schema](https://entire.io/api/openapi.json): inspected as discovery material, not assumed to be an approved third-party contract.

## Live access update — 12 September 2026

Full system access resolved the macOS Keychain restriction. `entire auth status` reports a valid login, and `npm run entire:check` passes live summary, search and history reads for the selected repository. Sanitized evidence: `tests/acceptance/results/M04/live-smoke.json`. This does not establish hosted scalability, an approved third-party Entire login, graph support, or the model-based 20-question semantic gate.

## Text model selection

`OPENAI_MODEL` remains the fast general-chat model. Optional `OPENAI_REPOSITORY_MODEL` selects a stronger model for conversations with a real connected repository, preserving the same context and authorized tool policy. The local test configuration uses `gpt-5.4-mini-2026-03-17` for ordinary chat and `gpt-5.4-2026-03-05` for repository conversations; Realtime voice is unchanged. The selection test passes through the actual SDK with a local HTTP fixture. Model capabilities and snapshot were checked in [OpenAI’s model documentation](https://developers.openai.com/api/docs/models/gpt-5.4); this choice is based on the recorded live evaluation, not a claim of universal model superiority.
