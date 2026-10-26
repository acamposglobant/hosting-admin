
---
name: security-review
description: Review a Hosting Admin change for vulnerabilities before merge, especially authentication, privileged operations and Linux integration.
---

# Security Review

1. Read AGENTS.md and docs/security/threat-model.md if present.
2. Inspect the complete diff and relevant surrounding code.
3. Trace untrusted inputs to sensitive operations.
4. Check authentication and authorization on every administrative endpoint.
5. Check shell invocation, filesystem access, service controls and Docker access.
   For Increment 2 mutation code, additionally verify:
   - The agent never runs as root and never trusts API-side validation —
     it re-validates the allowlist independently (D18).
   - Command construction is fixed argv with an absolute path; no shell,
     no interpolation, timeout with child kill (D19).
   - The unix socket has restrictive ownership/mode and no TCP listener
     exists (D17).
   - Confirmation tokens are single-use, session-bound, TTL-bounded, and
     never logged (D20, D25).
   - Every mutation step — including rejections — is audited (D21).
6. Check secret storage, logging and error responses.
7. Verify tests cover the important security boundaries.
8. Report findings with evidence and remediation steps.

Prioritize:
- Remote code execution.
- Privilege escalation.
- Authentication bypass.
- Unauthorized service or filesystem changes.
- Secret disclosure.
- Cross-site request forgery and injection.

Do not change production configuration.
Do not equate absence of findings with proof of security.
Do not modify code during a review unless explicitly asked to fix findings.
