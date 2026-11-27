---
name: agent-council
description: "Runs the evidence-based Agent Council (Lite by default, Full on request or Critical risk) for non-trivial vNext decisions and logs the verdict; 'chair queue' lists and closes open Chair rulings. Triggers: \"council\", \"karar verelim\", \"eklemeli miyim\", \"mimari karar\", \"chair queue\", or any question whose answer is a recommendation."
---

# Agent Council

The procedure. The contract — modes, roles, risk, verdicts, condition classes, Chair, amendments,
boundary — is [`docs/agent-council/README.md`](../../../docs/agent-council/README.md); read it once per
session and do not restate it. When the council fires and the disclosure duty:
[`.claude/rules/agent-council-plan-mode.md`](../../rules/agent-council-plan-mode.md).

Pick the mode from the request: **chair queue** when the user asks for it ("chair queue", "bekleyen
kararlar"); **Full** on "full council", Critical risk or an escalated Lite session; otherwise **Lite**.

## Lite (default)

1. **Check the log.** `docs/agent-council/sessions/README.md` — an existing row on the same question is
   amended, not re-decided.
2. **Frame it.** Create `ai-docs/agent-council/sessions/<YYYY-MM-DD-slug>/DECISION.md` from
   [`templates/DECISION.md`](../../../docs/agent-council/templates/DECISION.md); fill the question,
   acceptance, risk ([README § Risk](../../../docs/agent-council/README.md#risk)) and the requester.
3. **Evidence pack first.** Explore agents, the graph (`graphify`), `git log`/`git show --stat` for cost
   precedents, the relevant `/docs` page, [CHAIR.md precedents](../../../docs/agent-council/CHAIR.md#standing-precedents).
   Write verified facts (`file:line`) and assumptions into `DECISION.md`. **Test the premise** — a false
   premise ends the session as `VOID` with the finding, which is a result, not a failure.
4. **Independent proposals.** One `Agent` per role — Solution Architect, Pragmatic Engineer, Devil's
   Advocate, plus at most one conditional role from the README table — **all in one message** so they
   run in parallel and never see each other. Each prompt carries: its section of
   [`roles.md`](../../../docs/agent-council/roles.md), the question, the same evidence pack and
   [`templates/PROPOSAL.md`](../../../docs/agent-council/templates/PROPOSAL.md). Save each as
   `PROPOSAL-<nn>-<role>.md`.
5. **One objection round.** `SendMessage` each agent the other proposals; collect evidence-backed
   objections (claim, evidence, impact, requested revision). No new evidence → no objection. Stop.
6. **Decide.** Fill `DECISION.md`: one headline verdict, an item table if the question has parts,
   dissent (at least the Devil's Advocate's strongest unresolved argument — no loop back), conditions
   with class (`blocking` / `in-PR` / `follow-up`), the **next action**, verification and rollback.
   Facilitator = you; you own no proposal.
7. **Chair.** If the Chair is required, write at most five yes/no questions with a recommended answer;
   the status is `Awaiting Chair`. Only an explicit ruling by the Chair named in `CHAIR.md` closes it —
   record `Chair YYYY-MM-DD: <verdict>`; never infer a ruling from silence or from the request itself.
8. **Log.** Append one row above `<!-- newest at the bottom … -->` in
   `docs/agent-council/sessions/README.md`:
   `| YYYY-MM-DD | [<slug>](../../../ai-docs/agent-council/sessions/<slug>/DECISION.md) | topic | risk | verdict (+ status) | selected approach | blocking conditions / follow-up |`.
9. **Answer** the user: verdict, the next action, blocking conditions, Chair questions if any, the
   dissent in one line, and the session path.

## Full

As Lite, with these differences: roles by the README selection table (Test & Evidence included; High
4–7 roles, Critical all applicable plus a human expert, each with the blind spot it closes); step 5 has
a second round where each owner answers the objections against it with `ACCEPT | COUNTER |
DEFER_TO_EXPERIMENT | ESCALATE`; the decision carries the scored matrix
([README § Scoring](../../../docs/agent-council/README.md#scoring-full-optional-in-lite)). Reuse the
same agents through `SendMessage` so their context stays intact.

## Chair queue

1. List the log rows whose verdict cell says `Awaiting Chair` and has no later `Chair YYYY-MM-DD` entry.
2. For each, read the local `DECISION.md` if it exists and pull its Chair questions; when it is missing
   (another machine) or predates the questions block, derive at most three questions from the row's
   verdict and follow-up cells and say so.
3. Present them in one `AskUserQuestion` round (one question per decision, the recommended answer
   first); never decide for the Chair.
4. Write each answer as `Chair YYYY-MM-DD: <verdict>` into the row (and `DECISION.md` when present).
   A ruling that changes scope becomes a new Lite session instead.

## Repository specifics

- Code wins over docs (`LifecycleOrder.cs`, `PipelineExecutionProfile.cs`, `PipelineProfileResolver.cs`).
- Validation plans follow the Aether, outbox, multi-schema, logging and Result-pattern rules and the
  integration-test policy (`docs/testing/integration-testing.md`).
- A new component or task type records the coordination cost with the external
  `@burgan-tech/vnext-schema` package, `vnext-meta` and the Helm chart.
