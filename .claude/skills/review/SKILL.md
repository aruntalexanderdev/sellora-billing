---
name: review
description: Independent review of the current Sellora changes via the read-only code-reviewer agent.
argument-hint: [base-ref]
disable-model-invocation: true
---
Base ref: `$ARGUMENTS` (empty = `origin/dev`).

1. `git diff --stat <base>...HEAD` and `git status --short`. If nothing changed, say so and stop.
2. Spawn `code-reviewer` with: base ref, changed-file list, spec/phase reference if one exists.
3. Present findings (Blocker / Major / Minor) and the verdict. Offer to fix Blocker/Major; don't apply fixes without
   the user's go-ahead unless called from `/feature` or `/deliver-phase`.
