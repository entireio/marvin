# ADR 0004 — Entire local access and hosted access are separate integrations

Status: local adapter implemented; live acceptance pending; hosted adapter blocked on an approved service contract. 12 September 2026.

The user established that Entire login redirects through GitHub OAuth with the registered `entire-cli` client, PKCE and a temporary loopback callback. This demonstrates the CLI's login mechanism. It does not grant Marvin a third-party web client registration, authorize reuse of that client, or make website-session endpoints suitable as a service API.

The official Entire0.10.6 CLI is a supported local access mechanism. Marvin invokes an allowlist of read commands with explicit account context and repository scope. It does not install capture hooks, generate checkpoint summaries, run arbitrary commands, expose tokens to the browser, or reuse browser cookies. Process concurrency, output bytes, execution time and source reads are bounded. Per-request access checks precede cached source reads. This remains a **single-host local adapter**, not a scalable cloud architecture.

The runtime/UI depend on `RepositoryIntegration`, not subprocesses, credential-store details, or raw Entire responses. The current local adapter is refused when Marvin uses hosted OIDC authentication. A hosted integration must add an approved API adapter and credential/session lifecycle; changing an enum alone does not enable a hosted deployment.

Hosted readiness requires Entire to supply or confirm: application registration; server-side delegated authorization; account/repository identity and scopes; access and refresh-token storage/rotation; revocation and user disable behavior; supported read endpoints and pagination; error/rate-limit contracts. Then implement per-user encrypted credential storage with deployment key management, a bounded API client, distributed rate/concurrency controls where needed, tenant-keyed caches, and integration tests against two authorized accounts plus a denied account.

The published `https://entire.io/api/openapi.json` was inspected. Search explicitly describes website-session resolution, and parts of the repository gateway are described as passthrough/internal infrastructure. The schema lacks a complete external-client authentication contract. Public availability of a schema is not proof that its website endpoints are a stable third-party integration surface. The docs themselves recommend dedicated CLI commands over `entire api` internal routes.

Do not mark M4's hosted gates complete using CLI fixtures or a single user's keychain. Local live-read acceptance and hosted readiness are reported separately. Portal identity and Entire repository authorization remain distinct even if a future supported sign-in makes them feel seamless.
