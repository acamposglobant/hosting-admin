
# Hosting Admin Threat Model

## Assets

- VPS root privileges.
- Website files and customer data.
- Database credentials.
- TLS private keys.
- DNS and mail configuration.
- Backup data.
- Administrative sessions and audit records.

## Threat actors

- Unauthenticated Internet attackers.
- Attackers with stolen administrator credentials.
- Malicious or compromised hosted applications.
- Compromised dependencies or deployment credentials.
- Accidental operator actions.

## Trust boundaries

1. Internet to administrative interface.
2. Browser to API.
3. API to operating system.
4. API to privileged agent.
5. Hosting workloads to host system.
6. VPS to external backup storage.

## Required controls

- MFA and secure session management.
- Rate limiting and restricted network exposure.
- Least-privilege service accounts.
- Strict input validation and parameterized queries.
- Fixed allowlists for privileged operations.
- No arbitrary shell or unrestricted Docker socket access.
- Secret redaction and audit logging.
- External backups and tested restoration.
- Security updates and documented recovery.

## Initial scope

The first increment must be read-only.
No site creation, service restart, firewall modification, DNS mutation,
mail changes or privileged Docker operations.

## Increment 2: privileged agent and service mutations

Increment 2 introduces the first write capability: start/stop/restart of an
allowlisted set of systemd units through a separate local agent (plan
[increment-2-privileged-agent.md](../../.coda/plans/increment-2-privileged-agent.md),
decisions D16–D25).

### New trust boundary

- API ↔ agent over a Unix domain socket in a root-owned directory (mode 0770,
  group-restricted). The socket is never exposed on TCP or to other users.
- The agent authenticates the API with a shared secret (256-bit, file-based,
  0600); the API authenticates the agent by socket peer credentials.
- The agent runs as a dedicated unprivileged account and escalates only
  through a sudoers policy of enumerated fixed command lines — one line per
  allowlisted unit × action, no wildcards (D16). Note: the agent unit cannot
  use `NoNewPrivileges=yes` because the kernel ignores setuid under
  `NO_NEW_PRIVS`, which would break the sudo escalation path
  ([agent-systemd-unit.md](../deployment/agent-systemd-unit.md) §2).

### New threats and controls

| Threat | Control |
| --- | --- |
| Compromised agent account escalates beyond allowlist | Sudoers artifact enumerates complete fixed command lines only; no wildcards, no arguments (D16); validated by `visudo -cf` in CI (TA14) |
| CSRF-driven service mutation from a malicious page | Session cookie (SameSite=Strict) + double-submit CSRF header + Origin/Referer checks on every mutation endpoint (TA7) |
| Stolen or replayed confirmation token | Tokens are 256-bit random, hashed at rest, single-use, bound to the issuing session, 5-minute TTL (D20, TA9) |
| Mutation of a non-allowlisted unit | API-side allowlist check before any agent call (D18, TA8); agent re-checks its own allowlist (defense in depth) |
| Agent unavailable → silent partial state | Structured 503 `capability_unavailable`; no partial state; audited as Failure (D24, TA10) |
| Repeated agent failures flooding the audit log | Failure grouping: one row per failure episode transition, success doubles as recovery (T16) |
| Agent secret leakage via logs/audit/responses | Secret redaction tests extended (TA12, D25); secret lives only in a 0600 file and channel memory |

### Residual risks (increment 2)

- The agent account can stop/restart allowlisted services by design; a
  compromised agent can cause availability loss for those units (but not
  read data, change config, or touch non-allowlisted units).
- Confirmation tokens are valid for 5 minutes in the issuing session; an
  attacker who both hijacks the session and reads the token within the TTL
  can confirm a destructive action. Mitigated by session security (D11–D15)
  and the short TTL.
- Sudoers policy is static; adding a unit requires regenerating and
  reinstalling the artifact (deliberate: no runtime policy changes).
- On-target behavior (real systemd, real sudo) is validated only by the
  operator checklist; CI validates syntax and logic, not a live host.

## Residual risks

Document risks that cannot be fully mitigated in the initial increment.
Do not claim production readiness without validating the deployment.

The increment 1 security review pass logged the following residual risks
(full analysis in [security-review-increment-1.md](security-review-increment-1.md)):

- IAP integration validated only at first deployment (checklist in the
  deployment guide).
- Single admin with one in-application factor; MFA delegated to the IAP.
- Audit records live on the same host filesystem (SQLite); off-host audit
  shipping is a future control.
- No automated dependency vulnerability scanning yet.
- No protection is claimed against a root-compromised host.
