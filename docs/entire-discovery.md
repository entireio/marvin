# M0 — Entire integration discovery

Reviewed 12 September 2026 against [Entire documentation](https://docs.entire.io/) and its [documentation index](https://docs.entire.io/llms.txt). The product now documents code hosting, captured sessions/checkpoints, agentic search, and semantic graph. This does not establish a supported third-party identity provider contract for Marvin.

| Contract | Public evidence | Cloud decision | Local decision / dependency |
| --- | --- | --- | --- |
| Login / identity | [CLI login](https://docs.entire.io/cli-reference/login) describes browser login and device flow using Entire auth; [auth](https://docs.entire.io/cli-reference/auth) describes contexts and current identity | Approved external client registration, issuer/audience, scopes, callback, logout and consent contract still needed from Entire | Do not treat a CLI session token or unverified JWT decode as a web identity assertion |
| Tokens / refresh | Auth docs describe active context, token refresh and jurisdiction | No invented refresh endpoint or assumed token audience | Future supported CLI adapter may use explicit user-owned authorization; no ambient credential-store reads in this implementation |
| Repository discovery | [API](https://docs.entire.io/cli-reference/api) lists control-plane org/repository/mirror operations; stable repository IDs are documented | Confirm production supported API, permission model, pagination and rate limits | Prefer documented CLI commands where Entire recommends them |
| Source bounds / repo state | Repository/CLI docs expose repository workflows; internal API warning applies | M4 requires supported read contracts, revision/source bounds and tenant scopes | An allowlisted local CLI adapter remains feasible, not implemented/proven |
| Activity / checkpoints / sessions | [Activity](https://docs.entire.io/cli-reference/activity), [session](https://docs.entire.io/cli-reference/session), [checkpoint](https://docs.entire.io/cli-reference/checkpoint) describe read workflows | Confirm stability, access checks, result limits and service error semantics | M4 real-reference-repository tests required |
| Search / graph | [Search](https://docs.entire.io/cli-reference/search), [graph](https://docs.entire.io/cli-reference/graph) describe commands and graph plugin capabilities | Do not invent a graph REST endpoint | Graph is a CLI/plugin capability; deployment/install/authorization needs explicit integration design |
| Revocation / outages / limits | CLI context/token mechanisms are documented; no complete third-party service contract found | Entire must confirm revocation, user/account disable behavior and limits | Implement adapter errors and tests once supported contract is available |

The API documentation explicitly warns that underlying internal endpoints can change without notice and recommends dedicated CLI commands when available. Its examples are not adopted as Marvin production API guarantees.

**Decision:** implement an identity adapter with standards-based OIDC support and a clear loopback/local authentication fallback. Do not display “Continue with Entire” as a functioning login until Entire supplies an approved registration and demonstrated flow. Repo authorization stays separate from portal identity. Generic OIDC configuration is available; no live issuer was configured or tested here.

M0 discovery owner: project owner obtains supported third-party identity/read API guidance or authorizes a supported alternative. This is an external dependency, not a reason to block fixture-based M1–M3 engineering. M4 remains outside the current implementation scope.
