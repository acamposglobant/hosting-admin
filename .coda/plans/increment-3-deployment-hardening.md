# Hosting Admin — Increment 3 Plan: Deployment Hardening — First VPS Deployment (Rev 1)

**Status:** approved (2026-10-23). Implementation proceeds per the task sequence in section 9.
**No implementation is performed by this document.**

## 0. Decisions locked in this revision

| ID | Decision | Rationale |
|----|----------|-----------|
| D26 | Off-host audit shipping uses **local syslog** (`/dev/log`, RFC 3164/5424 text lines) written by a new .NET Worker (`AuditShippingWorker`), config-gated off by default. No message broker, no new infrastructure. | Project rules forbid message brokers without documented need. Ubuntu ships rsyslog by default; remote forwarding is a standard rsyslog config the operator already controls. The application's job is only to emit; transport to a second host is operator configuration. |
| D27 | Backup/restore is **operator-run scripts + documented procedure**, not an in-app feature. `deploy/backup.sh` (sqlite3 `.backup` + sha256 manifest) and `deploy/restore.sh` with explicit session-invalidation semantics. | Keeps the attack surface unchanged (no new endpoints, no new privileged paths). Backups are an operations concern; the app must not grow a backup API. |
| D28 | The `systemd-analyze security` review of the agent unit is a **documented first-deployment procedure with a recorded exposure score**, not a CI gate. | It can only run on a real host with the real unit. CI cannot validate it; a checklist + record sheet makes it an explicit deployment gate instead of a forgotten step. |
| D29 | Dependency vulnerability scanning enters CI as **advisory (non-blocking)** first: `dotnet list package --vulnerable --include-transitive` + `npm audit --omit=dev`. Failures are reported but do not fail the build until triaged. | Avoids CI breakage from unrelated advisories on day one; a follow-up can flip to blocking once the baseline is known. |
| D30 | The deployment guide becomes **versioned per increment** (`docs/deployment.md` updated in place, status header bumped to Increment 3). The agent deployment section is a first-class section, not an appendix. | Increment 2 shipped the agent but the guide still says "Increment 1, zero mutations" — it is now wrong in ways that matter at deployment time. |

## 1. Current repository state (start of Increment 3)

- Increment 1 (PRs #1–#14): read-only observability, auth, sessions, CSRF,
  audit trail, bootstrap, deployment guide v1.
- Increment 2 (PRs #15–#21): privileged agent (unix socket, shared secret,
  allowlisted `systemctl start/stop/restart`), confirmation flow, mutation
  endpoints, frontend service controls, audit integration with failure
  grouping, sudoers artifact + generator, agent systemd unit docs, security
  review fixes F1–F6.
- 18 API endpoints; 4 frontend screens; 4 SQLite tables (`audit_events`,
  `admin_state`, `sessions`, `service_action_confirmations`); 325 backend +
  51 frontend tests; 4 CI jobs (test-linux, test-windows, test-frontend,
  sudoers-validate).
- `docs/deployment.md` still describes only the Increment 1 topology and
  states "this increment performs zero mutations" — now stale.
- Deferred controls carried forward: off-host audit shipping, MFA (delegated
  to IAP), dependency scanning, `systemd-analyze security` review.

## 2. Assumptions

- A1. Target remains a single Ubuntu 24.04 VPS with an IAP in front of Nginx.
- A2. The operator installs and runs everything manually over SSH; no
  configuration management is introduced.
- A3. rsyslog is present and enabled on Ubuntu 24.04 Server by default; if the
  operator removed it, audit shipping degrades to disabled with a startup
  warning, never a crash.
- A4. `sqlite3` CLI is available on the VPS for backup/restore (standard on
  Ubuntu Server; the guide states it as a prerequisite).
- A5. No new ports are opened; off-host transport is the operator's existing
  syslog path, not a new listener.

## 3. Design

### 3.1 Deployment guide v2 (docs/deployment.md)

The guide is extended in place; existing sections stay, new sections cover
the agent path end to end:

- **Architecture diagram** gains the agent:

  ```
  hostingadmin-api.service ── unix socket (/run/hostingadmin/agent.sock, 0660)
                                       │
                                       ▼
                     hostingadmin-agent.service (unprivileged)
                                       │ sudo (enumerated lines only)
                                       ▼
                            systemctl start|stop|restart <unit>
  ```

- **New section: agent service account** — `hostingadmin-agent` system user,
  no login shell, no home directory creation beyond what the unit needs.
- **New section: shared secret provisioning** — `openssl rand -base64 48` →
  two mode-600 files (`/etc/hostingadmin/agent-secret` for the API,
  `/etc/hostingadmin/agent-secret` for the agent), owner root, group per
  service account, never in the repo, never in logs. Rotation procedure
  (write both files, restart both units, verify with a ping).
- **New section: socket directory** — `/run/hostingadmin` tmpfiles.d entry
  (root-owned, 0750, group = agent group), created before both units start.
- **New section: sudoers installation** — `visudo -cf` validation, then
  `install -m 0440` of the artifact; regeneration procedure when
  `Services:UnitNames` changes (points at `deploy/sudoers/generate.sh`).
- **New section: agent systemd unit** — the unit from
  `docs/deployment/agent-systemd-unit.md`, including the NoNewPrivileges
  rationale (kernel ignores setuid under NO_NEW_PRIVS).
- **Post-deployment checklist** gains: agent unit active, socket mode 0660,
  sudoers round-trip (`visudo -cf`), secret files mode 600, a real
  start/stop/restart of one allowlisted unit through the UI, audit rows
  visible for the mutation, and the systemd-analyze review (below).

### 3.2 systemd-analyze security review (D28)

`docs/deployment/systemd-security-review.md` documents:

- The exact commands: `systemd-analyze security hostingadmin-agent.service`
  and `... hostingadmin-api.service`, run on the VPS after install.
- How to read the exposure level and the per-directive breakdown.
- Acceptance: agent unit exposure must be recorded in the deployment log;
  any `EXPOSED` finding must be either fixed or explicitly accepted in
  writing in the doc's "first deployment record" table.
- A `deploy/systemd-security-check.sh` helper that runs both analyses and
  prints a summary the operator pastes into the record.

### 3.3 Off-host audit shipping (D26)

- New project `src/HostingAdmin.Worker/` (.NET Worker, already sanctioned by
  the architecture) hosting `AuditShippingWorker`: a
  `BackgroundService` that polls `audit_events` for rows newer than the last
  shipped id and writes each as one syslog line:
  `<13>Oct 23 12:00:00 hostingadmin-audit: {"id":..,"timestampUtc":..,"actor":..,"action":..,"outcome":..,"sourceIp":..,"detail":..}`
  (JSON payload, no shell, no interpolation of untrusted text into the
  priority line).
- **Cursor state** (last shipped id) lives in `admin_state`, so shipping
  resumes after restart without duplication or loss.
- **Config-gated**: `AuditShipping:Enabled` (default `false`),
  `AuditShipping:PollIntervalSeconds` (default 10). Disabled = no worker
  work, no syslog writes; the API is unaffected.
- **Failure behavior**: syslog write failure → log a warning, retry next
  poll; never crash the worker, never mutate audit rows. Shipping is
  best-effort duplication, not a transactional guarantee (documented).
- **Redaction**: the shipping path reuses the existing redaction rules; a
  portable regression test asserts no secret/token material can appear in
  shipped lines (extends TA12 discipline to the new sink).
- **Docs**: rsyslog remote-forwarding example (operator-run, e.g. one
  `@@` line to the log host) in the deployment guide.

### 3.4 Backup and restore (D27)

- `deploy/backup.sh`: `sqlite3 hostingadmin.db ".backup '...'"` to a
  timestamped file + `sha256sum` manifest; optional `--prune N` to keep the
  last N backups. Operator schedules it (cron example in docs). Runs as the
  API's service account or root — documented tradeoff (root: can read the
  DB regardless of ACLs; API account: least privilege, preferred).
- `deploy/restore.sh`: stop API → replace DB → start API → verify health
  endpoint + login. Explicitly documents that **restored sessions are
  invalid** (session hashes reference a live secret-free store; restoring an
  old DB resurrects old sessions — the procedure must prune the `sessions`
  table on restore, forcing re-login) and that confirmation tokens are
  short-TTL and self-expiring.
- Restore is **tested**: a CI job (ubuntu) creates a scratch DB via the
  existing test helpers, runs backup + restore scripts against it, and
  asserts the round trip (TA15).

### 3.5 Dependency vulnerability scanning (D29)

- CI job `deps-audit` (ubuntu-latest):
  - `dotnet list package --vulnerable --include-transitive` across the
    solution; non-zero findings → job prints them and exits 0 (advisory).
  - `npm audit --omit=dev` in `frontend/`; same advisory behavior.
- Output is a PR-visible warning; flipping to blocking is a one-line change
  deferred until the baseline noise is known.

## 4. API contracts

No new HTTP endpoints. The only observable API change: none (shipping is
invisible to clients; backup is operator-side). This increment is
deliberately docs + worker + scripts + CI.

## 5. Frontend changes

None.

## 6. Security analysis

- **New worker process**: runs under the API's existing unprivileged account
  (not root), reads only `audit_events`/`admin_state`, writes only to
  `/dev/log` (or journald socket). No new privilege, no new network listener.
- **Syslog sink injection risk**: audit detail fields are attacker-influenced
  (e.g. unit names from config, source IPs). The worker serializes a fixed
  JSON structure with `JsonSerializer` (no string building from raw input);
  newlines in detail are escaped by the JSON encoder, so line-framing
  injection into syslog is not possible. Test: detail containing `\n` and
  forged priority text ships as escaped JSON, one line.
- **Backup files contain audit + session hashes**: mode 600, operator-owned,
  documented storage location outside the repo; restore script warns that
  backups are sensitive.
- **No new sudoers grants, no new agent capabilities, no new endpoints** —
  the increment-2 security review conclusion (any new mutation re-enters
  review) is not triggered; this increment performs no mutations.

## 7. Required security tests

| ID | Requirement |
|----|-------------|
| TA15 | Audit shipping writes one syslog line per event, JSON-escaped, in id order; resume after restart does not duplicate or skip (cursor in `admin_state`). |
| TA16 | Shipping failure (syslog unavailable) is non-fatal: worker logs, retries, audit rows unaffected. |
| TA17 | Redaction: no secret/token material in shipped lines (portable test, runs on Windows CI too). |
| TA18 | Backup/restore round trip: restore produces a DB where health + login work and `sessions` is pruned (no resurrected sessions). |
| TA19 | `AuditShipping:Enabled=false` (default) produces zero syslog writes and zero worker activity. |

## 8. Threat model updates

- New: audit records now have an off-host copy (when enabled) — reduces the
  "attacker erases local audit" window; shipping is best-effort, not a
  guarantee; documented as such.
- New: backup files are a sensitive artifact (contain full audit history);
  storage/permissions/retention documented; loss of a backup = disclosure of
  audit metadata (no secrets: password hashes are salted+slow, session ids
  are hashed at rest).
- Residual risk retired: "off-host audit shipping is a future control" →
  replaced by the syslog shipping design + operator forwarding config.
- Residual risk added: syslog delivery is unacknowledged UDP-style
  (best-effort); a determined local attacker with root can still stop both
  copies (no protection against root compromise — unchanged).

## 9. Task sequence

| # | Task | Acceptance |
|---|------|------------|
| 1 | **Deployment guide v2** — agent account, secret generation/rotation, socket dir, sudoers install, agent unit, updated diagram + checklist (D30) | Docs complete; every command copy-pasteable; checklist covers agent + API + IAP; no secrets in examples |
| 2 | **systemd-analyze review procedure** — `docs/deployment/systemd-security-review.md` + `deploy/systemd-security-check.sh` (D28) | Script runs both units' analyses, prints summary; doc has acceptance criteria + first-deployment record table |
| 3 | **Audit shipping worker** — new Worker project, `AuditShippingWorker`, cursor in `admin_state`, syslog line protocol, config-gated (D26) | TA15–TA17 tests pass on Linux and Windows CI; default-off verified (TA19); docs updated |
| 4 | **Backup/restore scripts + restore test** — `deploy/backup.sh`, `deploy/restore.sh`, CI round-trip job (D27) | TA18 round-trip test green in CI; restore prunes sessions; docs updated |
| 5 | **Dependency scanning + increment security review** — `deps-audit` CI job (D29) + security review pass of all increment 3 changes | CI job present and advisory; review findings fixed or documented; docs updated |

## 10. Blockers

- None. All work is local (docs, worker, scripts, CI); no VPS access is
  required. The first real deployment remains an operator-run activity that
  consumes these artifacts.

## 11. Deviations from prior decisions (explicit)

- None. D15 exclusions (sites, DNS, mail, firewall, Docker) remain out of
  scope. No new privileged operations are introduced, so the increment-2
  rule ("any future mutation feature must re-enter the security review
  process") is not triggered.

## 12. Residual risks to document

- Syslog shipping is best-effort: no delivery acknowledgment, no replay from
  the remote side; gaps are possible under log-host outage (documented).
- Backups contain the full audit history; their storage location and
  retention are operator responsibilities.
- `systemd-analyze security` results are recorded at deployment time only;
  drift between deployments is not continuously monitored.
- Dependency scanning is advisory; a vulnerable dependency ships until
  triaged (baseline phase).
- The IAP integration remains validated only at first deployment.

---

## Statement

**No implementation was performed.** This document only drafts the Increment 3
plan; no code, no configuration, no VPS changes were made.
