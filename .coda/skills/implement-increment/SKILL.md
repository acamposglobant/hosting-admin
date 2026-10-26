
---
name: implement-increment
description: Implement a small, bounded Hosting Admin feature with tests, security checks and documented acceptance criteria.
---

# Implement Increment

1. Read AGENTS.md and relevant architecture documents.
2. Inspect the existing implementation and tests.
3. Identify the smallest useful change.
4. List affected files, risks and acceptance criteria.
5. Ask for clarification if requirements or security boundaries are ambiguous.
6. Implement only the approved scope.
7. Add unit and integration tests as appropriate.
8. Run the relevant build and test commands.
9. Review the diff for secrets, unsafe commands, unrelated changes and missing tests.
10. Update documentation when behavior or contracts change.

Never:
- Expand the scope silently.
- Claim tests passed without executing them.
- Modify a real VPS or production configuration without explicit approval.
- Introduce arbitrary shell execution.
- Bypass a failing security test.

Final report:
- Files changed.
- Behavior implemented.
- Commands executed and actual results.
- Security considerations.
- Remaining work.
