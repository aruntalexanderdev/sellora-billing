# Owner-proxy decision log

Decisions taken by the `owner-proxy` agent while the owner was offline (see `.claude/rules/owner-away.md`).
Newest at the bottom. The owner reviews each entry and sets **Status** to `✔ accepted` or `✘ reversed — <note>`.
The proxy reads this file before answering, so a reversal note works as guidance for future decisions.

## Template

```
### OP-<NNN> — <short title>  (<YYYY-MM-DD>, <branch / phase / feature>)
- Verdict: DECIDE | DEFER | ESCALATE    Category: <authority-matrix row>
- Question: <one line>
- Choice: <option taken / safe default / none>
- Reasoning: store-user: <…>; developer: <…>; risk: <low|medium|high>, reversible: <yes|no>
- Rejected: <option → reason>
- Affected files/commits: <paths or sha, if any>
- Owner review note: <what to double-check>
- Status: pending-owner-review
```

## Entries

_None yet._
