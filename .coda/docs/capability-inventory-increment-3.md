# Hosting Admin — Capability Inventory for Increment 3 Planning

**Date:** 2026-10-23  
**Status:** Increment 2 COMPLETE (all 7 tasks merged, PRs #15–#21)  
**Next:** Increment 3 planning

---

## 1. API Endpoints Summary

**Total:** 18 endpoints across 7 categories

### 1.1 Health & Diagnostics (No Auth)
- `GET /api/health` — Liveness probe
- `GET /api/diagnostics/force-error` — Test-only (dev only)

### 1.2 Authentication (Pre-Session)
- `POST /api/auth/login` — Admin login (password-based)
- `POST /api/auth/logout` — Revoke session

### 1.3 Bootstrap (Local-Only, Pre-Session)
- `POST /api/bootstrap/activate` — Open admin-creation window
- `POST /api/bootstrap/admin` — Create first admin credential

### 1.4 Observability (Read-Only, Session + CSRF)
- `GET /api/metrics` — CPU, memory, swap, uptime (10s cache)
- `GET /api/system/info` — Hostname, OS, kernel, architecture
- `GET /api/disks` — Mounted filesystems, usage
- `GET /api/services` — Systemd unit status + mutation capability flag

### 1.5 Service Mutations (Session + CSRF + Confirmation)
- `POST /api/services/{unit}/start` — Direct execution (non-destructive)
- `POST /api/services/{unit}/stop` — Returns confirmation token (destructive)
- `POST /api/services/{unit}/restart` — Returns confirmation token (destructive)
- `POST /api/service-actions/{id}/confirm` — Execute pending (single-use, 5-min TTL)
- `POST /api/service-actions/{id}/cancel` — Discard pending

### 1.6 Audit Log (Session + CSRF)
- `GET /api/audit` — Paged audit query (skip/take, max 200 per page)

### 1.7 Test-Only (Dev Only, Disabled by Default)
- `POST /api/test/sessions` — Issue fresh session
- `GET /api/test/sessions/current` — Validate session
- `POST /api/test/sessions/invalidate` — Revoke session

**Auth/CSRF:** All authenticated endpoints require session cookie + CSRF header. Loopback-only endpoints check actual connection IP.

---

## 2. Frontend Screens & Data Sources

### 2.1 Login Screen
- **File:** `frontend/src/screens/Login.tsx`
- **API:** `POST /api/auth/login`
- **Features:** Generic error message; rate-limit feedback

### 2.2 Dashboard Screen
- **File:** `frontend/src/screens/Dashboard.tsx`
- **APIs:** `/api/metrics`, `/api/system/info`, `/api/disks`, `/api/services`
- **Poll interval:** 10 seconds
- **Features:** Real-time observability; unavailable banners per data source; service action controls

### 2.3 Service Actions Component
- **File:** `frontend/src/screens/ServiceActions.tsx`
- **APIs:** `/api/services/{unit}/start|stop|restart`, `/api/service-actions/{id}/confirm|cancel`
- **Features:** Start (direct), stop/restart (confirmation dialog); hidden when capability unavailable

### 2.4 Audit Log Screen
- **File:** `frontend/src/screens/AuditLog.tsx`
- **API:** `GET /api/audit?skip=N&take=50`
- **Poll interval:** 30 seconds
- **Features:** Paged table; client-side action filter; pagination controls

### 2.5 App Root
- **File:** `frontend/src/App.tsx`
- **Features:** Navigation; auth state management; session check on mount

**Test Coverage:** 17 test files, ~2,037 lines total (Vitest + React Testing Library)

---

## 3. Privileged Agent Capabilities (Increment 2)

### 3.1 Agent Process
- **Project:** `src/HostingAdmin.Agent/` (Worker service)
- **Account:** Unprivileged `hostingadmin-agent`
- **Transport:** Unix domain socket `/run/hostingadmin/agent.sock` (mode 660)
- **Escalation:** Sudoers enumerated command lines (one per unit × action)
- **Hardening:** ProtectSystem=strict, ProtectHome=yes; **NO NoNewPrivileges** (breaks sudo)

### 3.2 Allowlist & Validation
- **Source:** Agent config (`Agent:UnitNames`), validated at startup
- **Rules:** Exact ordinal match; strict charset `[A-Za-z0-9_.@-]+`
- **Defense:** Agent re-validates every request independently

### 3.3 Command Execution
- **Command:** `systemctl <action> <unit>` (exactly 3 argv entries)
- **Actions:** `start`, `stop`, `restart` (fixed set)
- **Execution:** Process.Start with ArgumentList (no shell); hard timeout 10s; child tree killed on expiry
- **Result:** Structured `{ TimedOut, ExitCode, Output }`

### 3.4 Wire Protocol
**Request:** `{ "requestId", "action", "unit" }` (camelCase JSON)  
**Response:** `{ "requestId", "outcome": "Success|Failure|Rejected", "detail" }`  
**Auth:** Shared secret (256-bit, mode 600) as first line before JSON

### 3.5 Health Probe
- **Action:** `ping` (not a mutation, never exposed as API endpoint)
- **Purpose:** Authenticated liveness check; computes `mutationCapabilityAvailable` flag
- **Timeout:** 2s (shorter than mutation timeout)

### 3.6 Concurrency & Resource Limits
- Max concurrent connections: 2
- One request per connection
- Max request size: bounded
- No queue; excess connections rejected immediately

---

## 4. Persistence Schema (SQLite, WAL mode)

### 4.1 audit_events
- **Purpose:** Append-only audit log (90-day retention default)
- **Columns:** id, timestamp_utc, actor, action, outcome, source_ip, detail
- **Indexes:** timestamp_utc DESC, action
- **Events:** auth.*, bootstrap.*, service.*, audit.query

### 4.2 admin_state
- **Purpose:** Key-value store for bootstrap state and credentials
- **Keys:** `admin.credential_hash` (PBKDF2), `bootstrap.completed_utc`

### 4.3 sessions
- **Purpose:** Server-side session store (hash-at-rest, opaque id in cookie)
- **Columns:** id_hash (PK), admin_id, created_utc, expires_utc, idle_expires_utc, revoked
- **Lifecycle:** Insert on login; revoke on logout/idle; prune expired/revoked

### 4.4 service_action_confirmations
- **Purpose:** Single-use, session-bound, short-TTL confirmation tokens
- **Columns:** id_hash (PK, SHA-256), unit, action, session_hash, created_utc, expires_utc, consumed
- **Index:** expires_utc
- **Lifecycle:** Insert on stop/restart request (5-min TTL); mark consumed on confirm/fail; prune expired

---

## 5. Test Coverage Summary

### 5.1 Backend (.NET)
- **Status:** No dedicated test projects yet
- **Current:** Inline validation (allowlist, command construction, session lifecycle)
- **Planned (Increment 2 tasks):** Agent.Tests, Core.Tests, Api.Tests

### 5.2 Frontend (React + TypeScript)
- **Files:** 17 test files, ~2,037 lines
- **Coverage:** Screens (Dashboard, AuditLog, ServiceActions, Login), hooks (usePolling), API client, utilities
- **Framework:** Vitest + React Testing Library
- **Patterns:** Mock API responses, CSRF header verification, pagination, accessibility checks

---

## 6. Known Limitations & Deferred Items

### 6.1 Documented Limitations
- Audit data **not immutable** against host compromise (SQLite co-located)
- Off-host log shipping **deferred**
- Real IAP integration validated only at first deployment (not in CI)
- Confirmation tokens valid 5 min in issuing session (hijacked session + token read within TTL allows confirmation)
- Sudoers policy static; adding unit requires regenerating artifact
- On-target behavior validated only by operator checklist; CI validates syntax/logic only
- Agent account compromise yields exactly enumerated systemctl commands as root
- Audit remains co-located SQLite (off-host shipping still deferred)
- Agent unit **cannot use NoNewPrivileges=yes** (kernel ignores setuid under NO_NEW_PRIVS, breaking sudo)

### 6.2 Deferred to Increment 3+
- Off-host audit shipping
- Multi-admin support (schema reserved via `admin_id` field)
- MFA (architecture reserved)
- Polkit alternative to sudoers
- Site creation, DNS mutations, mail administration, firewall changes, Docker control (all D15/D23 exclusions)
- Backup/restore operations
- Log aggregation
- Metrics retention/export

### 6.3 Known Gotchas
- **CI Lesson:** Linux-only socket tests hide protocol bugs on Windows; every wire/protocol rule needs portable regression test
- **Deployment Lesson:** NoNewPrivileges + sudo assumption is WRONG; kernel ignores setuid under NO_NEW_PRIVS

---

## 7. Increment 2 Completion Status

**All 7 tasks merged (PRs #15–#21):**

| Task | PR | Status | Notes |
|------|----|---------|----|
| 1 | #15 | ✅ | Agent skeleton, unix socket, shared secret, systemd unit draft |
| 2 | #16 | ✅ | Allowlist, command construction, hard timeout, concurr
