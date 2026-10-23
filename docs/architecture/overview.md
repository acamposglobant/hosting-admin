
# Hosting Admin Architecture

## Purpose

A private administration panel for one Ubuntu Server VPS.
The initial increment provides read-only observability.

## Components

- React + TypeScript frontend.
- ASP.NET Core Web API on .NET 10 LTS.
- SQLite for application state and audit records.
- Linux metrics providers.
- A service-status provider for explicitly configured systemd units.
- A .NET Worker for future asynchronous operations.
- A restricted local agent for privileged operations (increment 2): a
  separate .NET Worker process listening on a Unix domain socket, running
  under a dedicated unprivileged account, escalating only through an
  enumerated sudoers policy (D16). The API talks to it over the socket with
  a shared secret; the agent re-checks its own allowlist independently.

## Architectural constraints

- Start with a modular monolith.
- Keep the API unprivileged.
- Do not expose the local agent over a public network.
- Never implement arbitrary shell execution.
- Keep hosting workloads separate from panel responsibilities.
- Do not assume Nginx, PHP, database, DNS, mail or Docker is installed.
- Use capability checks and report missing services clearly.

## Initial modules

1. Authentication and authorization.
2. Dashboard and metrics.
3. Service inventory and status.
4. Audit and diagnostics.
5. Service mutations via the privileged agent (increment 2): allowlisted
   start/stop/restart with a two-step confirmation flow for destructive
   actions (D20), full audit coverage with failure grouping (D21), and
   structured capability reporting when the agent is unavailable (D24).

## Future modules

- Websites and virtual hosts.
- PHP runtime management.
- Database administration.
- DNS and TLS.
- Docker applications.
- Mail administration.
- Backups and restoration.

## Operational requirements

- Structured logs.
- Health endpoints.
- Timeouts and cancellation.
- Explicit configuration.
- Tested backups and recovery.
- Documented installation and rollback procedures.

Deployment (IAP + Nginx + systemd, unprivileged user) is documented in
[docs/deployment.md](../deployment.md). The increment 1 security review pass
is in [docs/security/security-review-increment-1.md](../security/security-review-increment-1.md).
