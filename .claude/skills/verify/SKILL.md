---
name: verify
description: Build and test Sellora with compact output. Use after any code change before reporting done.
argument-hint: [unit|backend|frontend|all]
---
Run `scripts/verify.sh $ARGUMENTS` (no argument = all).
`unit` = backend build + unit tests only (no Docker); `backend` adds the Testcontainers integration tests; `all` = backend + frontend, like CI.

- Green: one line per part.
- Red: show only the errors printed, find the root cause, fix it if it's within the current task, re-run.
  Never skip or weaken tests. Report failures unrelated to the current change instead of fixing them.
