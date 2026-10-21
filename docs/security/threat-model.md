
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

## Residual risks

Document risks that cannot be fully mitigated in the initial increment.
Do not claim production readiness without validating the deployment.
