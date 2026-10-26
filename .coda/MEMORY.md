# MEMORY

This file stores durable learned context for the coding agent and the user.
Do not duplicate instructions already captured in `AGENTS.md`.

## Stable User Preferences

## Stable Project Preferences

## Important Decisions
- - Hosting Admin progress: Increments 1-2 COMPLETE (PRs #1-#21). Increment 3: tasks 1-4 COMPLETE, PR #22 MERGED (branch feature/increment-3-tasks-1-4, commits e89ba87, 8c3703a, 6d0ba96, 0b41ccc; suite 335 pass; CI green incl. backup-restore-test). Next: task 5 — advisory deps-audit CI job (D29: dotnet list package --vulnerable --include-transitive + npm audit --omit=dev, non-blocking) + security review pass of all increment 3 changes.
## Learned Facts

## Known Gotchas
- - CI lesson (increment 2): Linux-only socket tests hide protocol bugs on Windows — every wire/protocol rule needs a portable regression test (bit us twice: camelCase naming policy, ping-without-unit parse rule).
## Source-of-Truth Pointers
- - Hosting Admin progress: Increment 1 COMPLETE (16 tasks, PRs #1-#14 merged). Increment 2 plan drafted: .coda/plans/increment-2-privileged-agent.md (Rev 1, awaiting review) — privileged agent + service restarts, D16-D25, 7 tasks (agent skeleton, allowlist+commands, channel, confirmation+endpoints, frontend, audit, docs+review). Tasks 1-6 inline; new skill implement-mutation-task. Deviations: D12 evolved, D15 lifted for allowlisted start/stop/restart. Next: approve plan, then task 1.

## Things To Re-Validate
