# Hosting Admin — Increment 2 Plan: Privileged Agent + Controlled Service Restarts (Rev 1)

> Status: **Draft for operator review**
> Date: 2026-10-22
> Sources: `AGENTS.md`, `.coda/plans/increment-1-read-only-observability.md` (D1–D15),
> `docs/architecture/overview.md`, `docs/deployment.md`, `docs/security/threat-model.md`,
> `docs/security/security-review-increment-1.md` (residual risks).

---

## 0. Decisions locked in this revision

Numbering continues from Increment 1 (D1–D15 remain in force unless explicitly
evolved below).

| # | Decision |
|---|---|
| D16 | The privileged agent is a **separate .NET Worker process** (`HostingAdmin.Agent`) running under its own unprivileged service account (`hostingadmin-agent`) — never root, never the API's `hostingadmin` account. Escalation happens only through a **sudoers rule enumerating complete fixed command lines** (one per allowlisted unit × action, `NOPASSWD`); the agent process itself holds no privileges. Polkit is documented as an alternative and may be re-evaluated later, but sudoers-enumerated commands are the default (simpler to audit, no D-Bus surface). |
| D17 | **Transport (D5 honored):** the agent listens on a **Unix domain socket** at `/run/hostingadmin/agent.sock` (mode `660`, owned by the agent account with a shared group) — never a TCP socket, never a public interface. A **shared secret** (mode-600 file, generated at install) is required on every request as defense in depth. On Windows dev, the agent is not run; the API uses a fake channel returning a structured "unavailable" result. |
| D18 | **D12 evolves for mutations:** unit names may now arrive from the client, but **only as exact string matches against the server-side allowlist** (ordinal, case-sensitive, no wildcards, no traversal, strict charset). The allowlist — not client input — remains the control. The agent **independently re-validates** every request against its own allowlist copy and never trusts API-side validation (defense in depth across the trust boundary). |
| D19 | **Command construction:** the agent builds `systemctl start|stop|restart <unit>` with fixed argv (`ArgumentList`, `UseShellExecute=false`, absolute path `/usr/bin/systemctl`, hard timeout + cancellation). No shell, no interpolation into a command string, no arguments beyond the unit name and the fixed verb. |
| D20 | **Confirmation flow (D8):** `stop` and `restart` are destructive and require a two-step confirmation: the first request returns a single-use, short-TTL (default 5 min), session-bound confirmation token; execution happens only on explicit confirm. `start` is non-destructive (it can only bring a unit up) and executes directly. Confirmation tokens are single-use, expire, are bound to the creating session, and can be cancelled. |
| D21 | **Audit (D8 extended):** every mutation request (allowed and rejected), confirmation issuance, confirmation, cancellation, execution attempt, and agent outcome is audited with outcome and structured detail. The API audit store remains the source of truth; the agent additionally writes a minimal local operation log (journald via its own stdout) for cross-correlation — never secrets. |
| D22 | **Agent channel auth:** every request to the agent carries the shared secret; the agent rejects unauthenticated calls, applies per-call timeouts, and enforces a small concurrency cap. The socket path and secret are configuration-validated at agent startup (fail-fast). |
| D23 | **Scope:** mutations are limited to `start`/`stop`/`restart` of allowlisted systemd units. No enable/disable, no daemon-reload, no mask, no firewall, DNS, mail, Docker or site operations. Everything D15 excluded stays excluded. |
| D24 | **Capability gating:** if the agent is unreachable, unauthenticated, or times out, the API reports `capability_unavailable` (503) for mutations and the frontend disables the controls; service *status* (increment 1 read path) is unaffected. |
| D25 | **No new secrets in logs (D7 carries over):** the agent shared secret, confirmation tokens, and any credential material never appear in logs, audit records, or API responses; the existing redaction denylist is extended. |

---

## 1. Current repository state (start of Increment 2)

- Increment 1 complete and merged (PRs #1–#14): .NET 10 API (`src/HostingAdmin.Api|Core|Infrastructure`),
  React+TS frontend in `frontend/`, SQLite audit/sessions, error envelope,
  session lifecycle, CSRF, bootstrap, proxy attestation, metrics/services/audit endpoints.
- CI: GitHub Actions — `test-linux`, `test-windows`, `test-frontend` (all must be green).
- Security review pass for Increment 1: no open findings; residual risks logged
  (notably: "any future mutation feature must re-enter the security review process
  before implementation" — this plan is that re-entry).
- No agent project, no mutation endpoints, no confirmation storage exist yet.

## 2. Assumptions

- Same single-admin, IAP-fronted, loopback-bound deployment as Increment 1 (D1–D3).
- Ubuntu 24.04-class target with systemd; `sudo` available; polkit not required.
- The agent and API are deployed on the **same host** (unix socket assumption).
  A remote-agent deployment is out of scope and would require a new decision.
- The VPS is real: mutation tasks include an explicit operator-approved on-target
  validation step; no destructive command runs against production services during
  development (AGENTS.md).
- All example values in this document are illustrative (D11 carries over).

## 3. Design

### 3.1 Solution layout (additions)

```
src/
  HostingAdmin.Agent/            # NEW: standalone Worker (unix socket server, allowlist, runner)
  HostingAdmin.Api/              # adds mutation endpoints + confirmation flow (thin)
  HostingAdmin.Core/             # adds Agent/ (IAgentChannel, models), Services/ use-cases
  HostingAdmin.Infrastructure/   # adds UnixDomainSocketAgentChannel (Linux), NullAgentChannel (Windows)
tests/
  HostingAdmin.Agent.Tests/      # NEW: allowlist, command construction, socket auth, timeouts
  HostingAdmin.Core.Tests/       # confirmation flow use-case tests
  HostingAdmin.Api.Tests/        # endpoint + authz + audit integration tests
```

**Key rules:**
- The API never spawns `systemctl` for mutations and never holds sudo rights.
  All mutations cross the trust boundary through `IAgentChannel`.
- The agent never trusts the API: it re-validates the unit name against its own
  allowlist and its own strict charset rules before any process spawn.
- The agent has **no database, no session concept, no API credentials** — it knows
  nothing about users; authorization happens entirely in the API layer.

### 3.2 Trust boundary and network exposure (D5, D16–D18, D22)

```
Browser ──TLS──▶ IAP ──▶ Nginx ──▶ HostingAdmin.Api (127.0.0.1:5080, unprivileged)
                                        │  session + CSRF + audit (unchanged)
                                        │  unix domain socket (D5)
                                        ▼
                          /run/hostingadmin/agent.sock  (mode 660, agent-owned)
                                        │  shared secret (defense in depth)
                                        ▼
                     hostingadmin-agent.service (unprivileged, sandboxed)
                                        │  sudo: ONLY enumerated fixed command lines
                                        ▼
                     systemctl start|stop|restart <allowlisted unit>  (as root, via sudo)
```

- **Primary control:** the unix socket lives in a root-owned `/run/hostingadmin/`
  directory; file permissions (mode 660, shared group) decide who can connect —
  this is the network-isolation analogue of D2/D5. The agent never binds TCP.
- **Defense in depth:** shared secret on every request; the agent rejects calls
  without it (mirrors the proxy-attestation pattern of Increment 1).
- **Escalation surface:** exactly `2 × N` sudoers lines (N = allowlisted units ×
  start/stop/restart), each a complete fixed command line. There is no way to
  express "any unit" or extra arguments through this rule.
- The agent's systemd unit applies the same hardening profile as the API unit
  (NoNewPrivileges, ProtectSystem=strict, etc.) — `NoNewPrivileges` is compatible
  here because escalation is via `sudo` (a setuid binary), which
  `NoNewPrivileges` on the *agent* unit does not block for child exec; this is
  verified in task 1 and documented in task 7. If verification fails, the
  fallback is a dedicated polkit rule (documented alternative in D16).

### 3.3 Agent internals (D18, D19, D22)

- **Allowlist:** agent config (`Agent:UnitNames`, `Agent:SystemctlPath`,
  `Agent:Timeout`, `Agent:SecretFile`, `Agent:SocketPath`) validated at startup;
  startup fails on invalid entries (same posture as Increment 1's service allowlist).
- **Request shape:** `{ "requestId": "...", "action": "start|stop|restart", "unit": "nginx.service" }`
  over the socket, one JSON document per connection; response
  `{ "requestId": "...", "outcome": "Success|Failure|Rejected", "detail": "..." }`.
  `requestId` is echoed for correlation; the agent does not interpret it.
- **Validation (agent-side, independent):** action ∈ fixed set; unit must exactly
  match one allowlist entry (ordinal comparison) and pass the strict charset rule
  (`[A-Za-z0-9_.@-]+`, no `/`, no `\`, no whitespace, no leading dot) — belt and
  braces; the allowlist match alone is sufficient but both are tested.
- **Execution:** `Process.Start` with `ArgumentList` (`/usr/bin/systemctl`,
  action, unit), `UseShellExecute=false`, hard timeout (default 10 s) with
  cancellation, stdout/stderr captured and truncated, exit code mapped to outcome.
- **Bounded resources:** one request per connection, small max request size,
  concurrency cap (default 2), per-call timeout — no queue inside the agent.

### 3.4 Confirmation flow (D20)

- `POST /api/services/{unit}/stop|restart` (session + CSRF) creates a
  confirmation row and returns `{ "confirmationId", "unit", "action", "expiresAt" }`
  — it does **not** execute.
- `POST /api/service-actions/{confirmationId}/confirm` (session + CSRF) executes
  if: token exists, unconsumed, unexpired, and belongs to the current session.
- `POST /api/service-actions/{confirmationId}/cancel` discards it.
- Tokens are cryptographically random (≥ 128 bits), stored hashed (same posture
  as session ids), single-use (consumed on confirm **and** on failed execution),
  and pruned by the existing maintenance job.
- `start` executes directly (non-destructive) and is audited.

### 3.5 SQLite schema (addition)

```sql
CREATE TABLE IF NOT EXISTS service_action_confirmations (
    id_hash      TEXT PRIMARY KEY,     -- SHA-256 of the opaque confirmation token
    unit         TEXT NOT NULL,        -- allowlisted unit (exact match at creation)
    action       TEXT NOT NULL,        -- 'stop' | 'restart'
    session_hash TEXT NOT NULL,        -- creating session id hash (binding)
    created_utc  TEXT NOT NULL,
    expires_utc  TEXT NOT NULL,
    consumed     INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS ix_service_action_confirmations_expires
    ON service_action_confirmations (expires_utc);
```

Parameterized queries only; idempotent `SchemaInitializer` extension; cleanup in
the existing periodic maintenance path.

## 4. API contracts

| Method | Path | Auth | Notes |
|---|---|---|---|
| POST | `/api/services/{unit}/start` | session + CSRF | Direct execution; audited; 503 if agent unavailable |
| POST | `/api/services/{unit}/stop` | session + CSRF | Returns confirmation requirement; audited |
| POST | `/api/services/{unit}/restart` | session + CSRF | Returns confirmation requirement; audited |
| POST | `/api/service-actions/{id}/confirm` | session + CSRF | Executes if valid; single-use; audited |
| POST | `/api/service-actions/{id}/cancel` | session + CSRF | Discards token; audited |
| GET | `/api/services` | session + CSRF | Existing; gains `allowedActions: ["start","stop","restart"]` per unit and `mutationCapabilityAvailable` |

All mutation endpoints require the full Increment 1 stack: loopback binding +
proxy attestation (production) + valid session + valid CSRF token. Unit names
are accepted **only** as exact allowlist matches (D18); anything else → 404/400
without revealing the allowlist. Agent failures map to `capability_unavailable`
(503) or a generic `internal_error` — never agent internals in responses.

**Example shapes (illustrative — D11):**

```json
// POST /api/services/nginx.service/restart  → 200 (no execution yet)
{ "confirmationId": "9f3c...", "unit": "nginx.service", "action": "restart",
  "expiresAt": "2026-10-22T12:05:00Z" }

// POST /api/service-actions/9f3c.../confirm → 200
{ "requestId": "b1c2...", "unit": "nginx.service", "action": "restart",
  "outcome": "Success", "completedAt": "2026-10-22T12:01:10Z" }
```

## 5. Frontend changes (1 screen extended)

- **Services section of the Dashboard** gains per-unit action buttons
  (Start / Stop / Restart) gated by `allowedActions` and
  `mutationCapabilityAvailable`.
- Stop/Restart open a **confirmation dialog** showing unit + action, then call
  the two-step flow; Start executes directly.
- All requests carry the CSRF token (existing wiring); controls disabled with a
  capability banner when mutations are unavailable; result feedback surfaces
  success/failure generically. Vitest + React Testing Library component tests.

## 6. Security analysis

| Threat | Control |
|---|---|
| Internet attacker reaches the agent | Unix socket in root-owned `/run/hostingadmin/`, mode 660; agent never binds TCP (D5, D17) |
| Compromised API process mutates arbitrary units | Agent re-validates against its own allowlist (D18); sudoers enumerates only fixed full command lines (D16) — even a fully compromised API can only run the exact pre-enumerated command lines |
| Command injection via unit name | Exact allowlist match + strict charset (D18); fixed argv, no shell (D19); tested with injection/traversal payloads |
| CSRF-driven mutation | Existing CSRF + Origin validation applies to all new POST endpoints; confirmation adds a second deliberate step (D20) |
| Stolen session performs silent mutations | Confirmation tokens are session-bound, single-use, short-TTL; every step audited (D20, D21) |
| Replay of a captured agent request | Shared secret + single-use confirmation semantics; socket peer access restricted by file permissions; per-request `requestId` correlation |
| Agent DoS / resource exhaustion | One request per connection, max request size, concurrency cap, hard timeouts (D22) |
| Secret leakage | Agent secret and confirmation tokens never logged; redaction denylist extended (D25) |
| Audit gap | Every mutation step audited in the API store (D21); agent writes its own journald line for cross-correlation |
| Agent runs as root | It does not: unprivileged account + enumerated sudo escalation only (D16); verified by tests and deployment checklist |

## 7. Required security tests

**Agent (new `HostingAdmin.Agent.Tests`)**
- TA1. Request without the shared secret → rejected, no process started.
- TA2. Unit not in the agent allowlist → rejected, no process started (mocked launcher).
- TA3. Injection/traversal payloads (`nginx.service; reboot`, `../../evil`, whitespace,
  unicode) → rejected by charset + allowlist; no process started.
- TA4. Process started with `ArgumentList`, `UseShellExecute=false`, absolute path,
  fixed subcommand, hard timeout applied; timeout → structured failure, process killed.
- TA5. Malformed/oversized request → connection closed with rejection, no process.
- TA6. Concurrency cap enforced; excess requests rejected cleanly.

**API (existing suites extended)**
- TA7. Mutation endpoints: 401 without session, 403/401 without CSRF, audited rejections.
- TA8. Non-allowlisted unit at the API layer → 4xx, **no call to the agent channel** (fake channel asserts zero calls).
- TA9. Confirmation flow: token required for stop/restart; expired, consumed,
  foreign-session, or unknown token → rejected and audited; confirm executes exactly once.
- TA10. Agent unavailable/timeout → 503 `capability_unavailable`, mutation audited as Failure, no partial state.
- TA11. Every mutation step (request, confirmation issuance, confirm, cancel, outcome) writes exactly one audit row; failure grouping applies to repeated agent failures.
- TA12. Agent secret and confirmation tokens never appear in logs, audit rows, or responses (redaction tests extended).

**Integration (Linux CI)**
- TA13. Real unix-socket round trip: auth → allowlisted request → fake `systemctl` shim → structured outcome.
- TA14. Sudoers policy file (shipped as a doc artifact) is validated in CI by a
  structural test (every allowlisted unit × action appears as a complete fixed
  command line; no wildcards).

## 8. Threat model updates

- **Trust boundary 4 (API → privileged agent)** becomes real in this increment:
  unix socket + file permissions (primary), shared secret (defense in depth),
  agent-side independent allowlist re-validation, fixed argv, timeouts.
- **New asset exposure:** service availability (stop/restart of allowlisted units).
  Bounded by: allowlist minimality, confirmation flow, full audit, and the fact
  that the agent can do nothing beyond the enumerated sudoers lines.
- **New residual risks:** a compromised API session can stop/restart allowlisted
  services (mitigated by confirmation + audit, not eliminated); a compromised
  agent account can run exactly the enumerated systemctl commands as root (that
  is the granted power — minimize N); sudoers/polkIt policy correctness is
  deployment-validated (task 7 checklist); the agent widens the host-attack
  surface by one privileged escalation path (kept minimal and sandboxed).
- Threat model doc gains a "Privileged agent" section; deployment doc gains the
  agent unit, socket setup, and sudoers policy.

## 9. Task sequence

Each task is one PR-sized slice on its own feature branch (per project workflow:
branch from updated master, security review after each task, CI green before merge).

| # | Task | Acceptance check | Suggested agent |
|---|---|---|---|
| 1 | **Agent process skeleton + trust boundary** — `HostingAdmin.Agent` Worker; unix socket listener (Linux) with shared-secret check; startup config validation (fail-fast); systemd unit draft with hardening; `NullAgentChannel` for Windows dev; `NoNewPrivileges` interaction with sudo verified | Agent starts, listens only on the unix socket; unauthenticated request rejected (TA1); config errors fail startup; tests green on Linux CI; Windows build/test green with null channel | dotnet-engineer |
| 2 | **Allowlist + command construction (agent-side)** — agent allowlist config + strict charset validation; fixed-argv `systemctl` builder; hard timeout + kill; structured request/response; concurrency cap | TA2–TA6 pass; startup fails on invalid allowlist; no shell anywhere (code review + tests); timeout kills the child and returns a structured failure | dotnet-engineer |
| 3 | **API→agent channel with auth** — `IAgentChannel` in Core; `UnixDomainSocketAgentChannel` in Infrastructure (secret header, per-call timeout, capability reporting); DI wiring; `mutationCapabilityAvailable` surfaced on `GET /api/services`; agent health probe | TA13 passes on Linux CI; Windows returns structured "unavailable" (D24); secret never logged (TA12 partial); no mutation endpoint exists yet | dotnet-engineer |
| 4 | **Confirmation flow + mutation endpoints** — confirmation store (schema above), token issue/confirm/cancel, session binding, single-use + TTL; `POST /api/services/{unit}/start|stop|restart` and confirm/cancel endpoints with full authz + CSRF; API-side allowlist enforcement (D18) | TA7–TA9, TA12 pass; non-allowlisted unit never reaches the channel (TA8); expired/consumed tokens rejected; maintenance job prunes expired tokens | dotnet-engineer |
| 5 | **Frontend service controls UI** — action buttons gated by `allowedActions` + capability; confirmation dialog for stop/restart; two-step flow wiring; generic error/success states; CSRF on all calls | Component tests: dialog gating, confirm/cancel paths, disabled-when-unavailable; build green; no new XSS sinks (grep-verified) | dotnet-engineer |
| 6 | **Audit integration** — audit events for every mutation step (request, issuance, confirm, cancel, outcome) with failure grouping for repeated agent failures; audit screen renders mutation actions; retention applies | TA10–TA11 pass; poll-burst style test shows no duplicate rows; audit screen shows actions with outcome and source IP | dotnet-engineer |
| 7 | **Docs + security review pass** — deployment doc: agent systemd unit, socket dir, service account, sudoers policy artifact + install steps; threat model update (§8); architecture overview update; full security review of tasks 1–6 against D16–D25; on-target validation checklist (operator-run, explicit approval) | Docs complete; review findings addressed or logged in `docs/security/security-review-increment-2.md`; full suite green on Linux CI (test-linux + test-windows + test-frontend); sudoers artifact validated (TA14) | security-reviewer (review) + dotnet-engineer (docs) |

Tasks 1–6 are verifiable on the Windows dev machine (fakes/null channel) plus
Linux CI for socket/systemd-specific traits. Task 7's on-target validation runs
only on the real VPS with explicit operator approval (AGENTS.md).

## 10. Blockers

| # | Blocker | Blocks | Does NOT block |
|---|---|---|---|
| B1 | Operator approval of this plan (esp. D16 sudoers-vs-polkit and D18's D12 evolution) | All tasks | Nothing — this is the gate |
| B2 | Linux CI runner socket-permission behavior for unix sockets | Task 1 final verification | Tasks 2–6 development |
| B3 | VPS availability + operator approval for live mutation validation | Task 7 on-target checklist | Tasks 1–6 development |

## 11. Deviations from Increment 1 decisions (explicit)

| Ref | Change | Rationale |
|---|---|---|
| D12 | Evolved: unit names may now arrive from the client, but **only** as exact matches against the server-side allowlist (was: "never accepted from the client"). The allowlist remains the control; the agent re-validates independently. | Mutations require identifying a target unit; exact-match allowlisting preserves D12's intent (client input never expands what can be executed). |
| D15 | Narrowly lifted for this increment: start/stop/restart of allowlisted systemd units only. Every other D15 exclusion (sites, DNS, mail, firewall, Docker) remains out of scope (D23). | This is the agreed Increment 2 scope. |
| D15/D8 | Mutations now exist, so the D8 "administrative mutations" audit policy becomes active, plus the confirmation flow (D20) that Increment 1 only reserved. | Required by the security review's non-claim: "any future mutation feature must re-enter the security review process" — task 7 is that review. |

All other Increment 1 decisions (D1–D11, D13, D14) carry over unchanged.

## 12. Residual risks to document

- A stolen, unexpired admin session can initiate (and confirm) mutations within
  the confirmation window — bounded by session security, CSRF, short token TTL,
  and full audit; not eliminated.
- The agent's sudoers grant is root execution of enumerated commands; a
  compromise of the agent account yields exactly that power and nothing more,
  but it is real power — keep the allowlist minimal.
- Sudoers/polkit policy correctness is validated at deployment, not continuously.
- The agent shares the host's fate; no protection against a root-compromised host.
- Audit remains co-located SQLite (off-host shipping still deferred).
- `systemd-analyze security` review of the agent unit happens at first deployment.

---

## Statement

**No implementation was performed.** This document only drafts the Increment 2
plan; no code, no service accounts, no sudoers rules, and no VPS changes were made.
