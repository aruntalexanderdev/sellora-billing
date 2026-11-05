# Agent Coordination

- Main session orchestrates; every task has one owner. Subagents never spawn agents, commit, or push.
- Inspect existing code before editing. Follow the approved spec; never invent missing business rules — list them as open questions.
- Sequence: requirements → DB design → backend → frontend → QA + review → final verification.
- Gates: DB design approved before any migration. API contract approved before frontend integration
  (frontend may scaffold screens against the draft contract, but not wire it up).
- Run independent work in parallel (max 2 agents). Never let two agents edit the same files.
- Everything must build and tests must pass. Never skip, delete, or weaken a failing test to get green — report it.
- Code review is mandatory for M and L work.
- Never overwrite unrelated user changes; check `git status` before starting.
- Record architectural decisions in the feature spec's "Decisions" section — not in new files.
