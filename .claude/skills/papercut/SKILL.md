---
name: papercut
description: "File a friction report when a documented command, flag, skill step, rule or doc fights you (same error twice, wrong doc, silent failure), then keep working; 'papercut triage' groups reports into fixes. Triggers: \"papercut\", \"papercut triage\", or any repeated failure that is not the code under change."
---

# Papercut

Agents notice friction first: a flag the help lists and the tool rejects, a command that exits 0 on
an error, a doc that the code contradicts, a skill step that no longer matches the repo. A papercut
records it once, in a fixed shape, so the next session finds the workaround and an owner can fix the
cause. Reports live in `ai-docs/papercuts/` (git-ignored, local); fixes land in the committed
guidance through triage.

## File one

File when **any** holds — and the cause is the environment or the guidance, not the change you are
making:

- the same error twice with no progress, or more than a few turns spent on one tool;
- a documented command, flag, port, path or step does not work as written;
- a doc, rule or skill says something the code contradicts;
- a tool or script reports success while it failed, or fails without saying why.

Steps:

1. `grep -rl "<the exact error text>" ai-docs/papercuts/ 2>/dev/null` — a match means it is known:
   use its workaround, add `seen: YYYY-MM-DD` to that file, and stop here.
2. Write `ai-docs/papercuts/YYYY-MM-DD-<slug>.md`:

   ```markdown
   ---
   area: <skill name | rule file | doc path | script | MCP server | external tool>
   signature: "<exact error text or symptom, one line>"
   status: open
   ---
   Tried: <command or step, as run>
   Expected: <what the guidance said would happen>
   Happened: <what happened, with the relevant output lines>
   Workaround: <what got you unblocked, or "none">
   Fix: <the change that would remove it — which file, which line>
   ```

   No secrets, tokens, personal data or customer data in a report; redact them.
3. Tell the user in one line that a papercut was filed, then continue the task with the workaround.
   Filing never blocks the task and never edits guidance on its own.

## Triage (on request)

1. Read `ai-docs/papercuts/*.md` with `status: open`; group by `area` + `signature`.
2. For each group, propose one target:
   - a row in that skill's **When it fails** table;
   - a correction to the rule, doc or skill text that is wrong (the code wins);
   - a script or tool fix (clearer flag, error message, non-zero exit);
   - a new case in [`docs/agent-council/evals.md`](../../../docs/agent-council/evals.md) when the
     friction was a council decision going wrong;
   - an upstream issue draft as local markdown when the cause is a sibling repository.
3. Show the proposals in one list and apply only what the user approves. Mark each handled report
   `status: fixed` with the file it changed, or `status: wontfix` with the reason.
