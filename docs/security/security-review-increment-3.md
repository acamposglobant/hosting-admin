# Security Review — Increment 3 (Deployment Hardening)

**Date:** 2026-10-26
**Scope:** all increment 3 changes (PR #22 + task 5): deployment guide v2,
systemd security review procedure, audit shipping worker (D26), backup/restore
scripts (D27), advisory dependency scanning (D29).
**Reviewer:** security review pass per project workflow (task 5).
**Result:** no blocking findings. Two observations documented below; both are
accepted as-is with rationale.

## 1. What was reviewed

| Area | Files | Verdict |
|------|-------|---------|
| Audit shipping worker (D26) | `src/HostingAdmin.Worker/*`, `Program.cs` wiring, `appsettings.json` | Pass |
| Audit store extension | `IAuditStore.QueryAfterIdAsync`, `SqliteAuditStore` | Pass |
| Backup/restore scripts (D27) | `deploy/backup.sh`, `deploy/restore.sh`, `deploy/test-backup-restore.sh` | Pass (2 observations) |
| CI additions | `backup-restore-test` job, `deps-audit` job (D29) | Pass |
| Docs | `docs/deployment.md`, `docs/deployment/systemd-security-review.md`, threat model | Pass |

## 2. Worker security analysis (D26)

- **No new attack surface:** no endpoints, no network listeners, no new
  privileges. The worker runs inside the API process under the existing
  unprivileged service account; its only sink is a unix **datagram** socket
  send to `/dev/log` (no TCP, no shell, no external process).
- **Config-gated off by default** (`AuditShipping:Enabled=false`, TA19);
  options validated at startup (`ValidateOnStart`, min poll interval 1 s,
  min batch 1).
- **Line-framing injection (TA15/TA17):** the syslog line is a fixed prefix
  (`<13>hostingadmin-audit: `) plus a JSON payload serialized with
  `JsonSerializer` — never string-built from raw input. Newlines and control
  characters in attacker-influenced fields (actor, detail, source IP) are
  escaped by the JSON encoder, so one event is always exactly one line and
  no forged priority/tag can appear. Covered by portable tests (run on
  Windows CI too).
- **Priority/tag are compile-time constants** — no event content reaches the
  priority line.
- **SQL:** all queries parameterized (`QueryAfterIdAsync`, cursor upsert in
  `admin_state`). Cursor is an integer id, parsed with
  `CultureInfo.InvariantCulture`.
- **Failure behavior (TA16):** sink/store failures are logged as warnings
  and retried next poll; the cursor never advances past unsent rows; audit
  rows are never mutated. Shipping is best-effort by design (documented).
- **Resume semantics (TA15):** cursor persisted per batch after successful
  writes; restart resumes without duplication or loss.

## 3. Backup/restore security analysis (D27)

- **No new in-app surface:** operator-run scripts only; no endpoints, no
  sudoers changes, no agent capabilities (D27 keeps the attack surface
  unchanged).
- **No shell injection surface:** no `eval`, no command construction from
  untrusted input. Paths are operator-supplied positional arguments — the
  same trust level as the operator typing the command; documented defaults.
- **Backup artifacts are sensitive** (full audit history + session hashes):
  written mode 600 into a mode-700 directory; storage outside the repo and
  retention are documented operator responsibilities.
- **Restore prunes `sessions` before the API starts** (TA18): a restored
  database can never resurrect old sessions; every admin must re-login.
  Covered by the CI round-trip test.
- **Integrity check:** the manifest hash is compared against the hash of the
  file being restored (not a `sha256sum -c` filename lookup, which would
  verify the wrong file when a backup is copied/renamed — fixed during
  development and covered by the tamper-rejection test).
- **Stale WAL/SHM sidecars are removed** before the restored file is used,
  preventing reconciliation against old WAL frames.

### Observations (accepted, no change required)

1. **`restore.sh` trusts its operator.** The script runs as root (stops the
   API, writes the DB path). Its inputs are operator-supplied paths; an
   operator can already do everything the script does. Documented in the
   script header; no untrusted input reaches it.
2. **`backup.sh`/`restore.sh` assume `sqlite3` CLI** (documented
   prerequisite, A4). The scripts fail with a clear message when absent —
   no silent fallback to a weaker path.

## 4. CI additions

- `backup-restore-test` (task 4): round-trip + tamper rejection + prune
  semantics; shellcheck on all three deploy scripts.
- `deps-audit` (task 5, D29): `dotnet list package --vulnerable
  --include-transitive` + `npm audit --omit=dev`, **advisory** (`|| true`).
  Workflow-level `permissions: contents: read` applies; no secrets in scope.
- Baseline at introduction (2026-10-26): **zero vulnerable packages**
  (verified locally on the full solution and frontend production deps).

## 5. Findings summary

| ID | Severity | Finding | Disposition |
|----|----------|---------|-------------|
| R1 | Info | `restore.sh` is a root-trust operator tool; no additional hardening needed beyond docs | Accepted |
| R2 | Info | Advisory scanning ships vulnerable deps until triaged (D29, deliberate) | Documented in threat model; flip to blocking later |

No blocking findings. No new mutation features were introduced, so the
increment-2 rule ("any future mutation feature must re-enter the security
review process") is not triggered.
