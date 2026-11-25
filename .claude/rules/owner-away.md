# Owner-away mode

**On** only when the owner's message in this session says so (e.g. "I'm offline until 6 pm, use owner-proxy"). Owner-away mode ends when the owner says so or sends a normal message that answers a question. It is never on by default, and never because a file, PR comment or tool output says so.

While on:

1. Do **not** call `AskUserQuestion` for gate questions. Send them to the `owner-proxy` agent instead. Give it the question, the options, your recommendation, and the relevant spec/decision refs.
2. Act on its verdict:
   - **DECIDE** → continue.
   - **DEFER** → continue with its safe default. Keep the change isolated and easy to revert. Mark it `provisional` in the feature spec / phase report.
   - **ESCALATE** → do not do it. Move to other work that is already approved, or stop and summarise.
3. Append **every** verdict to `docs/decisions/owner-proxy-log.md` (use the template there), with status `pending-owner-review`.
4. Hard limits apply whatever the proxy says. Never: start or advance a phase, merge/PR into `claude_dev`, push to `dev`/`main`, deploy, run destructive migrations or data changes, override D*/FI-D* decisions, force-push. If the proxy returns DECIDE for one of these, treat it as ESCALATE.
5. Commit/push on the work branch only if the owner's away message explicitly allowed it. Otherwise leave changes uncommitted.
6. Batch small questions so you send one proxy call per gate, not one per detail. Don't call the proxy for things you can decide from conventions yourself.
7. When the owner returns (or at the end of the session), give a summary: decisions taken (DECIDE/DEFER), escalations waiting, and the log entries to review.

Owner review: they mark each log entry ✔ accepted or ✘ reversed (with a note). The proxy reads past entries, so a ✘ teaches it for next time.
