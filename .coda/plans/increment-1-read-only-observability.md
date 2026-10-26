# Hosting Admin — Increment 1 Plan: Read-Only VPS Observability (Rev 3)

> Status: **Final review corrections applied — awaiting explicit operator approval**
> Date: 2026-10-21 (rev 3)
> Sources: `AGENTS.md`, `docs/architecture/overview.md`, `docs/security/threat-model.md`,
> solution-architect and security-reviewer analyses, operator revision directives (rev 2 + rev 3).

---

## 0. Decisions locked in this revision

| # | Decision |
|---|---|
| D1 | Identity-aware access proxy (IAP) with MFA fronts the panel in production; application-level authentication is retained as **defense in depth** until a later explicit decision changes it. |
| D2 | **Network isolation is the primary control**: the API binds to loopback when the proxy runs on the same host. Proxy attestation is defense in depth, never a substitute for network isolation. If a private interface is required, network restrictions are documented and mTLS is evaluated. |
| D3 | Forwarded headers (`X-Forwarded-*`) are trusted **only** from explicitly configured proxy addresses. Client-supplied identity headers (e.g. `X-Forwarded-User`) and proxy-attestation headers are **never trusted from clients** and are stripped/overwritten at the proxy; the API rejects or ignores them from any other source. |
| D4 | Origin validation **and** explicit CSRF protection for all cookie-authenticated requests (not SameSite alone). |
| D5 | Privileged agent (future) never bound to a public network interface. |
| D6 | First-admin bootstrap: **disabled by default**; requires explicit local activation for a limited initialization window; local-only authorization validates the actual remote connection address (`RemoteIpAddress`), never forwarded headers; completion is persisted and bootstrap cannot be reopened through any ordinary API request. Never a public registration page. |
| D7 | Never log passwords, session identifiers, session secrets, proxy secrets, or any credential material. |
| D8 | Audit: all login attempts, logouts, authorization failures, administrative mutations, relevant access events, and operational failures — with **grouping/rate-limiting of repeated identical provider failures** (audit state transitions, significant failures, and recovery). **No audit row per dashboard poll.** |
| D9 | Audit retention: 90 days default, operator-configurable; **SQLite audit data is explicitly not immutable against host compromise**; off-host shipping deferred. |
| D10 | Dashboard polling: 10 seconds; bounded caching, timeouts and concurrency on metrics. |
| D11 | Metrics are detected at runtime; all example values in this document are illustrative, never configured defaults. |
| D12 | systemctl: fixed-argument, read-only subcommands, timeout-bounded, server-side allowlist only; unit names never accepted from the client. |
| D13 | **CI is an early task**: portable tests run on every PR; Linux integration tests run on a Linux CI runner. The production VPS is never the routine CI environment. |
| D14 | Development prerequisites (skeleton, health, error handling, tests, CI) are implemented **before** the full authentication workflow. Production proxy protection is not claimed until the trusted-proxy mechanism is implemented and tested. |
| D15 | Scope stays read-only: no site creation, service restarts, DNS, mail, firewall or privileged Docker operations in Increment 1. All privileged operations and writable server-management features are out of scope. |

---

## 1. Current repository state

- **Tracked files:** only `AGENTS.md`. Untracked: `.coda/` and `docs/`.
- **`src/` and `tests/` are empty** — no solution, no `.gitignore`, no manifests, no CI.
- Greenfield; docs and this plan are the only design artifacts.

## 2. Prerequisites — separated by environment

### 2.1 Development prerequisites (needed to start coding)

| Item | Needed for |
|---|---|
| .NET 10 solution + `Api`/`Core`/`Infrastructure` projects + xUnit test projects | Everything |
| `.gitignore` (VS/Rider, Node, SQLite files) | Repo hygiene |
| Frontend scaffold (Vite + React + TS) | Screens |
| Linux CI runner + workflow (early task, not deployment) | D12/D14 |
| Procfs fixture files for parser tests | D12 |

### 2.2 Production deployment decisions (deferred; do not block development)

| Item | Needed by |
|---|---|
| Choice of identity-aware access proxy product and its MFA mechanism | Deployment (task 15) |
| Proxy→API trust mechanism confirmation (default: loopback binding + shared-secret header; mTLS evaluated if a private interface is required) | Deployment (task 15) |
| VPS availability for on-target validation | Deployment time |

**Rule (D14):** no production proxy protection is claimed in docs or code comments until
the trusted-proxy mechanism is implemented and its tests pass.

## 3. Revised architecture

### 3.1 Solution layout (unchanged in shape)

```
src/
  HostingAdmin.Api/             # thin endpoints, auth, middleware, DI composition root
  HostingAdmin.Core/            # use-cases + interfaces: Metrics/, Services/, System/, Audit/, Auth/, Errors/
  HostingAdmin.Infrastructure/  # Linux/ (procfs parsers, systemctl runner), Null/ (Windows stubs), Sqlite/
tests/
  HostingAdmin.Core.Tests / .Api.Tests / .Infrastructure.Tests
```

**Key rule:** no endpoint touches `/proc`, `/sys`, or processes directly — everything goes
through `Core` interfaces; the app is testable on Windows with fakes and fixtures.

### 3.2 Network exposure and proxy trust (D1–D3, D5)

```
Internet ──TLS──▶ Identity-Aware Access Proxy (MFA)
                        │  (same host: loopback; or documented private interface)
                        ▼
                 Nginx reverse proxy ──▶ HostingAdmin.Api
                        (binds 127.0.0.1/loopback by default — primary control)
                        (proxy attestation header — defense in depth)
                                          ├─▶ SQLite (audit, sessions)
                                          └─ (future) privileged agent — loopback/unix
                                               socket only, never public
```

- **Network isolation first (D2):** the API binds to loopback when the proxy is on the
  same host — this is the primary access control. If a private interface is ever required,
  the network restrictions (firewall rules, security-group scope) must be documented and
  **mTLS between proxy and API evaluated** before adoption.
- **Proxy attestation is defense in depth (D1):** a shared-secret header set only by the
  trusted proxy adds a second layer; it never substitutes for loopback binding.
- **Header hygiene (D3):** the proxy strips or overwrites client-supplied
  `X-Forwarded-*` and any identity/attestation headers before forwarding. The API trusts
  forwarded headers only from explicitly configured proxy addresses (validated against
  the actual connection remote address). Identity is established **only** by the
  application's own session — never from a client-supplied header.
- The privileged agent (future increments) is never exposed on a public interface.

### 3.3 Authentication and identity (D1, D3, D7)

- **IAP ↔ application session relationship (documented):** the IAP authenticates the
  human at the edge (MFA) and forwards the request over a trusted channel. The IAP's
  assertion is **not** an application identity: the application maintains its own
  credential check and its own session, so a valid IAP session without an application
  session reaches only the login page. Compromise of either layer alone does not yield
  panel access. Application authentication remains defense in depth unless a later
  explicit operator decision changes it.
- **No client-supplied identity headers (D3):** the API never derives authorization from
  headers like `X-Forwarded-User`; such headers from non-proxy sources are rejected.
- **Application auth:** opaque server-side session cookie (`HttpOnly`, `Secure`,
  `SameSite=Strict`), single admin, Argon2id/PBKDF2 hash, login rate limiting, generic
  failure messages.
- **CSRF (D4):** explicit antiforgery token required on all cookie-authenticated requests,
  in addition to `SameSite=Strict`; `Origin`/`Referer` validated against the configured
  origin; mismatches rejected.
- **Secret hygiene (D7):** passwords, session identifiers, session secrets, proxy secrets,
  and credential material never appear in logs, audit records, or responses; redaction
  denylist in the logging pipeline.

### 3.4 Bootstrap (D6)

- **Disabled by default.** Bootstrap exists only after an operator explicitly activates it
  **from the local console/localhost** (e.g. a CLI command or local-only activation
  request) which opens a **limited initialization window** (configurable, short).
- **Local-only authorization** validates the actual remote connection address
  (`RemoteIpAddress` on the connection) — forwarded headers are never used to authorize
  local access.
- On success: admin credential stored (hashed), completion persisted in `admin_state`;
  the window closes and bootstrap **cannot be reopened through any ordinary API request** —
  re-activation requires local console access again.
- No public first-run registration page exists at any time.
- All bootstrap attempts (activation, success, failure, post-completion attempts) audited.

### 3.5 Session lifecycle (new)

- **Identifier:** opaque random session id (cryptographic RNG, ≥ 128 bits entropy);
  the cookie carries the raw id; the database stores only its **hash** (hashed at rest) —
  a DB leak does not yield usable session cookies.
- **Expiration:** absolute expiration (configurable, e.g. 12 h) enforced server-side;
  optional idle expiration (configurable, off by default initially).
- **Revocation:** logout revokes the session row immediately and clears the cookie
  (`Set-Cookie` with expired attributes); revocation is checked on every request.
- **Association:** each session row is associated with the administrator identifier
  (`admin`), enabling future multi-account or per-session audit correlation.
- **Cleanup:** expired/revoked sessions are pruned by the same periodic maintenance job
  that prunes audit retention.
- **Cookie:** `HttpOnly`, `Secure`, `SameSite=Strict`; rotation on login.

### 3.6 Metrics collection without shell execution (D10–D12)

- **CPU/mem/swap/load/uptime/OS:** read fixed, known-safe files
  (`/proc/stat`, `/proc/meminfo`, `/proc/swaps`, `/proc/loadavg`, `/proc/uptime`,
  `/etc/os-release`) with defensive parsers. Never enumerate other processes.
- **Disks:** `/proc/mounts` filtered to real filesystems + `statvfs` (libc P/Invoke).
- **systemd status (D12):** `systemctl show` with fixed argv (`ArgumentList`,
  `UseShellExecute=false`, absolute path, read-only subcommands only, hard timeout +
  cancellation); unit names come **only** from a server-side allowlist validated at
  startup; the client never supplies unit names. D-Bus remains the later replacement
  behind the same interface.
- **Runtime detection (D11):** providers detect actual CPU, memory, disk and service
  information at runtime; capability checks report missing sources clearly. Every numeric
  value in this document is illustrative only.
- **Bounded resources (D10):** short-TTL metrics cache, per-operation timeouts,
  capped concurrent collections, 10 s dashboard polling.
- **Windows dev:** `Null` providers return a structured "unavailable" result.

### 3.7 Audit policy (D8, D9)

**Audited events:** all login attempts (success/failure), logouts, authorization failures
(including proxy-bypass and non-local bootstrap attempts), administrative mutations (none
exist in this read-only increment — policy established now), relevant access events
(first access per session, audit-log views), and operational failures.

**Failure-event discipline (new):** repeated identical provider failures are
**grouped or rate-limited** — the audit trail records the **state transition** into
failure, significant failures, and **recovery**, not every repeated occurrence.

**Not audited:** routine dashboard polling (`/api/metrics`, `/api/disks`, `/api/services`
reads within an active session).

**Mechanics:** parameterized queries only; bounded pagination (`take` capped at 200);
retention prune at startup + periodic, default **90 days**, operator-configurable;
append-only (no update/delete paths); DB file outside web root, owned by the unprivileged
service account.

**Explicit limitation:** SQLite audit data is **not immutable** against host compromise;
off-host log shipping is deferred to a later increment.

## 4. API contracts

| Method | Path | Auth | Notes |
|---|---|---|---|
| POST | `/api/auth/login` | none (IAP + rate limit) | Sets session cookie; audited |
| POST | `/api/auth/logout` | session | Revokes session; audited |
| POST | `/api/bootstrap/activate` | **local console/localhost only** | Opens limited init window; audited |
| POST | `/api/bootstrap/admin` | **loopback only**, window open only | One-time first-admin init; audited; cannot reopen via API |
| GET | `/api/health` | none | Minimal liveness: status only, no internals |
| GET | `/api/metrics` | session + CSRF | CPU, memory, swap, load, uptime; cached; **not audited per poll** |
| GET | `/api/system/info` | session + CSRF | hostname, OS, kernel, architecture |
| GET | `/api/disks` | session + CSRF | mount, device, fs, usage |
| GET | `/api/services` | session + CSRF | configured units only; `capabilityAvailable` flag |
| GET | `/api/audit?skip&take` | session + CSRF | paged; `take` capped at 200; audited access |

All non-health endpoints require: network isolation (loopback binding) + proxy attestation
(production, defense in depth) + valid application session + valid CSRF token. Unit names
are never a client parameter anywhere.

### Example shapes (illustrative values only — D11)

**`GET /api/metrics`** (200):

```json
{
  "cpu": { "usagePercent": 12.4, "cores": 2, "loadAverage1m": 0.31, "loadAverage5m": 0.22,
           "loadAverage15m": 0.18, "sampledAt": "2026-10-21T12:00:00Z" },
  "memory": { "totalBytes": 2147483648, "usedBytes": 1073741824, "availableBytes": 1073741824, "usagePercent": 50.0 },
  "swap": { "totalBytes": 536870912, "usedBytes": 0, "usagePercent": 0.0 },
  "uptimeSeconds": 864000
}
```

**`GET /api/services`** (200) — units come from server config; a missing unit is reported,
not hidden:

```json
{
  "services": [
    { "name": "nginx", "loadState": "loaded", "activeState": "active", "subState": "running", "enabledState": "enabled", "since": "2026-10-20T08:14:03Z" },
    { "name": "notinstalled", "loadState": "not-found", "activeState": "inactive", "subState": "dead", "enabledState": "unknown", "since": null }
  ],
  "capabilityAvailable": true
}
```

### Error envelope (all non-2xx)

```json
{ "error": { "code": "validation_failed", "message": "One or more parameters are invalid.",
             "details": [ { "field": "take", "message": "Must be between 1 and 200." } ],
             "traceId": "8f3c..." } }
```

Codes: `validation_failed` (400), `unauthorized` (401), `forbidden` (403 — includes
proxy-bypass rejection and non-local bootstrap attempts), `not_found` (404),
`rate_limited` (429), `internal_error` (500), `capability_unavailable` (503).
No stack traces, paths, versions, or secrets in responses.

### SQLite schema

```sql
CREATE TABLE IF NOT EXISTS audit_events (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp_utc TEXT    NOT NULL,
    actor         TEXT    NOT NULL,
    action        TEXT    NOT NULL,
    outcome       TEXT    NOT NULL,             -- 'Success' | 'Failure'
    source_ip     TEXT,
    detail        TEXT
);
CREATE INDEX IF NOT EXISTS ix_audit_events_timestamp ON audit_events (timestamp_utc DESC);
CREATE INDEX IF NOT EXISTS ix_audit_events_action    ON audit_events (action);

CREATE TABLE IF NOT EXISTS admin_state (
    key   TEXT PRIMARY KEY,   -- e.g. 'bootstrap_completed', 'bootstrap_window_expires'
    value TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS sessions (
    id_hash      TEXT PRIMARY KEY,          -- SHA-256 of the opaque session id (hashed at rest)
    admin_id     TEXT NOT NULL,             -- associated administrator identifier
    created_utc  TEXT NOT NULL,
    expires_utc  TEXT NOT NULL,             -- absolute expiration
    idle_expires_utc TEXT,                 -- optional idle expiration
    revoked      INTEGER NOT NULL DEFAULT 0
);
```

WAL mode; idempotent `SchemaInitializer`; retention prune (default 90 days, configurable);
expired/revoked session cleanup in the same periodic maintenance path.

## 5. Frontend screens (3)

| # | Screen | Shows |
|---|---|---|
| 1 | **Login** | Credentials form (behind IAP in prod); error on failure |
| 2 | **Dashboard** | CPU, memory/swap, uptime, OS/kernel, disks, service list; **10 s polling**; capability banners |
| 3 | **Audit log** | Paged table; filter by action |

Plain fetch + polling hook; CSRF token attached to all cookie-authenticated requests;
no Redux/React-Query. Vitest + React Testing Library.

## 6. Updated threat model notes

- **Trust boundary 1 (Internet → admin interface):** IAP provides MFA; **network isolation
  (loopback binding) is the primary control**, proxy attestation is defense in depth —
  neither substitutes for the other.
- **Header spoofing:** client-supplied identity and attestation headers are stripped at
  the proxy and never trusted by the API from non-proxy sources.
- **CSRF:** explicit tokens + Origin validation + `SameSite=Strict` (defense in depth).
- **Bootstrap risk:** mitigated by default-disabled bootstrap, explicit local activation
  with a limited window, `RemoteIpAddress`-based local authorization (forwarded headers
  never used for local-only authorization), and persisted completion state.
- **Session theft:** bounded by absolute expiration, revocation, hashed-at-rest ids.
- **Audit:** signal-preserving (auth events, authz failures, mutations, state transitions,
  significant failures, recovery); per-poll noise excluded; retention bounds growth;
  storage explicitly non-immutable against host compromise.
- **Residual risks:** stolen session before expiry (lifetime limits + revocation reduce,
  not eliminate); compromised host compromises panel, sessions and audit trail; audit
  co-located with workloads until off-host shipping (deferred); `/proc` exposure without
  `hidepid=2`; dependency 0-days; TLS/IAP posture validated only at real deployment;
  single admin = no separation of duties; `systemctl` spawn is the one execution point.

## 7. Required security tests (updated)

**Auth & session**
- T1. Every admin endpoint returns 401 without a valid session (endpoint-matrix test).
- T2. Invalid, **expired (absolute)**, optionally **idle-expired**, or **revoked** session
  → 401, no data in body.
- T3. Cookie flags asserted: `HttpOnly`, `Secure`, `SameSite=Strict`; logout clears the
  cookie and revokes the row.
- T4. Login lockout/rate limit after N failures; each failure audited without recording
  the password.
- T5. **Proxy-bypass (defense in depth):** in production configuration, a request lacking
  valid proxy attestation is rejected and audited — even with a valid session cookie.
  Documented as a secondary control behind loopback binding.
- T6. **Header trust:** a client-supplied identity header (`X-Forwarded-User`) or
  attestation header from a non-proxy source is rejected/ignored; forwarded headers are
  honored only from explicitly configured proxy addresses.
- T7. **Bootstrap:** disabled by default; requires explicit local activation; works only
  during the window and only from the actual local connection address; after completion
  it returns 403/410 and cannot be reopened via any API request; all attempts audited.

**Session lifecycle**
- T8. Session expires at absolute expiration; expired session id is rejected.
- T9. Logout revokes the session; the revoked id is rejected immediately.
- T10. Expired/revoked session rows are pruned by the cleanup job.
- T11. Only the session-id **hash** is stored; the raw id never appears in the database
  or logs.

**CSRF & Origin**
- T12. Cookie-authenticated requests without a valid CSRF token are rejected (4xx).
- T13. Cross-origin request with disallowed `Origin` receives no
  `Access-Control-Allow-Origin` and is rejected.

**Audit (D8)**
- T14. Login success/failure, logout, authorization failure each write exactly one audit row.
- T15. **Repeated dashboard polling writes no audit rows** (poll-burst test).
- T16. **Repeated identical provider failures are grouped/rate-limited**; a state
  transition into failure and the recovery are audited, not every occurrence.
- T17. Audit is append-only; no update/delete path exists.
- T18. Retention prune removes events older than the configured window; default 90 days;
  configuration validated at startup.

**Secrets & errors**
- T19. No password, session id, session secret, proxy secret, or connection string appears
  in any log, response, or audit record (login success + failure + forced error + metrics).
- T20. Forced exception → generic envelope, no stack trace/paths/versions; `traceId` in log.

**Process safety (D12)**
- T21. Non-allowlisted unit name → 4xx and **no process started** (mocked launcher).
- T22. Process started with `ArgumentList`, `UseShellExecute=false`, absolute path,
  read-only subcommand, timeout applied.

**Input & data**
- T23. Malformed input → 400; sanitized in audit (log-injection test).
- T24. Parameterized queries only; pagination bounded (`take` ≤ 200).

**Transport & platform**
- T25. Production: HTTP→HTTPS redirect, HSTS, secure cookies; forwarded headers trusted
  only from the configured proxy.
- T26. **CI:** portable tests run on every PR; Linux integration tests (procfs, statvfs,
  systemctl) run on a Linux CI runner; the production VPS is never the routine CI
  environment. Parser tests pass everywhere against fixtures (incl. malformed lines).

## 8. Residual risks to document

- Stolen session before expiry (lifetime limits + revocation reduce, not eliminate).
- Compromised host compromises panel, sessions and audit trail; **SQLite audit data is
  not immutable** (documented; off-host shipping deferred).
- Audit DB co-located with hosted workloads until shipped off-host.
- `/proc` exposure without `hidepid=2` is a host-configuration matter.
- Dependency 0-days; TLS/IAP posture validated only at real deployment.
- Single admin = no separation of duties.
- `systemctl` process spawn is the one execution point (fixed argv, allowlist, timeout).
- Proxy attestation is defense in depth only; if the API is ever bound to a non-loopback
  interface, mTLS must be evaluated and network restrictions documented (D2).

## 9. Revised task sequence (Rev 3)

Development prerequisites come first; authentication follows; production deployment
decisions are separated and do not block development.

| # | Task | Acceptance check |
|---|---|---|
| 1 | **Solution skeleton** + `/api/health` + test projects + `.gitignore` | `dotnet build`/`test` green; health JSON returned |
| 2 | **Error envelope + structured logging + traceId + secret redaction** | Forced exception → 500 envelope, no internals in body; redaction denylist verified (D7) |
| 3 | **Linux CI setup (early)** — CI pipeline running portable tests on every PR; Linux runner for integration traits | CI green on a sample PR; Linux integration trait executes on the Linux runner; VPS never used as CI |
| 4 | SQLite infra + audit store + retention prune (parameterized, idempotent) | Temp-DB insert/read-back; prune honors configured window (default 90 d); schema idempotent |
| 5 | **Session lifecycle** — opaque ids, hashed-at-rest, absolute + optional idle expiration, revocation, cookie invalidation, admin association, cleanup job | T8–T11 pass: expiration, revocation, cleanup, hash-only storage |
| 6 | Auth: login/logout on top of session lifecycle, rate limit, audit of auth events | 401 envelope unauthenticated; cookie flags asserted; failed login audited; 429 after N attempts |
| 7 | CSRF protection + Origin validation on cookie-authenticated requests | Tokenless requests rejected; disallowed Origin denied |
| 8 | First-admin bootstrap — disabled by default, explicit local activation, limited window, `RemoteIpAddress` local-only check, persisted completion | T7 passes: non-local rejected; window enforced; completion persisted; cannot reopen via API |
| 9 | **Network isolation + trusted-proxy enforcement** — loopback binding default; proxy attestation as defense in depth; forwarded headers only from configured proxy addresses; strip/ignore client identity headers | T5/T6 pass: bypass rejected + audited; spoofed headers ignored; **no production proxy protection claimed in docs until this task's tests pass** |
| 10 | Metrics providers + procfs parsers + fixtures + bounded cache/timeouts/concurrency + endpoint | Parser fixture tests; contract JSON; Windows → "unavailable" shape; burst requests bounded |
| 11 | System info + disks (os-release parser, statvfs, mount filter) | Pseudo-filesystems excluded; contract shapes |
| 12 | Service status provider (fixed-argv systemctl, allowlist, capability check) | T21/T22 pass; not-found unit reported; startup fails on invalid allowlist |
| 13 | Audit query endpoint + selective audit-on-access + failure grouping/rate-limiting | T14–T16 pass; paging validated |
| 14 | Frontend scaffold + Login + CSRF wiring + auth flow | Build green; login + CSRF component tests |
| 15 | Dashboard + Audit screens (10 s polling, cards, tables) | Component tests; manual e2e |
| 16 | Deployment docs (IAP + Nginx + systemd unit, unprivileged user) + security review pass | Docs updated; findings addressed or logged; full suite green on Linux CI |

Tasks 1–13 are backend-only, verifiable on the Windows dev machine (fixtures/fakes);
Linux-only integration traits (procfs, statvfs, systemctl) run on the Linux CI runner
from task 3 onward (D12). Task 9's proxy-attestation tests run in a production-like
configuration on CI; the real IAP integration is validated only at deployment (task 16).

## 10. Assumptions

- Single admin; IAP product provides MFA at the edge; application auth is defense in
  depth (D1) and remains so unless a later explicit decision changes it.
- Same-origin deployment (SPA served behind the same proxy) — CORS deny-by-default with
  an explicit dev-origin allowlist.
- Ubuntu 24.04-class target; `/proc` formats stable; `systemctl` at a fixed path.
- The VPS is real but this increment performs zero mutations — safe by construction.
- All example metric values in this document are illustrative (D11).

## 11. Blockers and what they actually block

| # | Blocker | Blocks | Does NOT block |
|---|---|---|---|
| B1 | Choice of identity-aware access proxy product and its MFA mechanism | Task 16 (deployment docs); final IAP integration validation | Tasks 1–15 (development proceeds against the attestation abstraction) |
| B2 | Proxy→API attestation mechanism confirmation (default: loopback binding + shared-secret header; mTLS evaluated if a private interface is required) | Task 9 final design detail; task 16 | Tasks 1–8; task 9 can start with the default proposal |
| B3 | Linux CI runner availability | Task 3 (CI task) — if unavailable, fixture tests still gate PRs locally and the trait runs later | Tasks 1–2, 4–15 development |
| B4 | VPS availability for on-target validation | Production-readiness claim only (task 16 / deployment) | All development tasks |

No blocker prevents starting tasks 1–5.

## 12. Remaining security risks and design decisions

- **Proxy attestation is defense in depth, not a substitute for network isolation** —
  loopback binding is the primary control (D2). If a private interface is ever required,
  mTLS must be evaluated and network restrictions documented.
- **IAP vs application session relationship** is documented (§3.3): the IAP is not an
  application identity; the application session is independently required. Any future
  change (e.g., trusting IAP-injected identity) requires an explicit operator decision
  and a security review.
- **Bootstrap window** introduces a small operational risk (window left open); mitigated
  by short default window, local-only activation, and persisted completion state.
- **Session idle expiration** is optional and off by default initially — operator may
  enable it later.
- **Audit failure-grouping threshold** (how many identical failures before collapsing)
  is a tunable design detail to be fixed during task 13.
- **`systemctl` process spawn** remains the one execution point; D12 mitigations apply;
  D-Bus replacement deferred behind the same interface.
- **Production-readiness** is not claimed until task 9's trusted-proxy tests pass and
  task 16's deployment validation completes on the real VPS.

---

## Statement

**No implementation was performed, no code was written, no privileged commands were
executed, no VPS or deployment configuration was modified.** This revision changes only
the plan document.
