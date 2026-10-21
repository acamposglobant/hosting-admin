
# Hosting Admin — Project Instructions

## Project goal

Build a private, self-hosted Linux VPS administration panel.
This is a personal operations tool, not a replacement for cPanel.

The first increment is READ-ONLY VPS observability.

## Architecture

- Frontend: React, TypeScript, Vite.
- Backend: ASP.NET Core Web API on .NET 10 LTS.
- Persistence: SQLite for application state and audit records.
- Background processing: .NET Worker when asynchronous operations are introduced.
- Privileged operations: separate, restricted local agent.
- Deployment: Ubuntu Server, systemd and Nginx.
- Tests: xUnit for .NET; frontend tests appropriate to the chosen stack.

Keep the initial solution small. Prefer a modular monolith over microservices.

## Security — non-negotiable

- Never run the API, frontend or worker as root.
- Never implement arbitrary shell execution.
- Never concatenate untrusted input into shell commands.
- Never expose the privileged agent directly to the Internet.
- Never expose secrets, passwords, private keys or tokens in logs or API responses.
- Require authentication and authorization for all administrative endpoints.
- Use secure session handling, CSRF protection where applicable, and MFA
  for remote administrative access.
- Validate all inputs on the server.
- Use least privilege for files, processes, service accounts and database access.
- Do not change firewall rules, SSH configuration, DNS, mail or production
  services without an explicit approved task.
- Do not execute destructive commands or modify a real VPS during development
  without explicit approval.
- Do not disable security controls to make tests pass.

## Engineering standards

- Use clear module boundaries and dependency injection.
- Keep controllers thin; put use-case logic in the application layer.
- Use asynchronous I/O where appropriate.
- Validate configuration at startup.
- Return structured errors without leaking internal details.
- Add tests for every new use case and security boundary.
- Use parameterized database queries.
- Avoid unnecessary dependencies and infrastructure.
- Do not add Docker, message brokers or distributed systems without a
  documented need.

## Initial increment

Implement read-only server observability:
- CPU, memory, swap, disk and uptime.
- Operating system information.
- Status of known systemd services.
- API health and structured error handling.
- Authenticated API access and an audit trail for administrative access.

Do not implement site creation, service restarts, firewall changes, DNS
mutations, mail administration or arbitrary Docker control in this increment.

## Required workflow

1. Inspect the existing repository before making changes.
2. Explain the proposed changes and relevant risks.
3. Implement one bounded increment at a time.
4. Run tests and builds relevant to the change.
5. Review the diff for security issues and unrelated changes.
6. Document assumptions, commands, results and remaining risks.
7. Never claim a test passed unless it was actually executed successfully.

## Definition of done

- Build succeeds.
- Relevant automated tests pass.
- Security-sensitive behavior has explicit tests.
- Documentation is updated.
- No secrets or unrelated generated files are committed.
- The final report lists changes, validation results and known limitations.
