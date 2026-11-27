# Council regression cases

The council's most valuable output in its history is **refuting a premise**: in five of the logged
sessions the question rested on something that turned out false. These cases test whether a changed
skill, contract or CI prompt still finds that. The method follows the usual skill-eval practice: cases
come from real sessions, the expected finding is fixed in advance, and a change is compared against the
version before it.

## Method

- **Ablation decides defaults.** The same question is run with the variant and without it (Lite vs
  Full, skill text before vs after a change) and a blind judge — a fresh agent that does not know which
  is which — picks the better decision against the expected finding. Record both runs.
- **Mixed suite as it grows.** Golden cases (below: fixed input, expected finding) are the small fixed
  core; add real council questions from new log rows graded on *trajectory* — did the run read the code
  before deciding, test the premise, name the next action — and a few cases the skill author grades.
  A missed premise or a stalled verdict in a real session becomes a new case.
- **Expect noise.** Council runs are non-deterministic; run a case at least twice. A suite every run
  passes every time is probably not sensitive enough; a pass rate around 70–90% that moves with a change
  is the useful signal.

## When to run

- After changing `.claude/skills/agent-council/SKILL.md`, `README.md`, `roles.md` or `.github/ai/council.md`.
- Before changing the default mode: run every case in Lite **and** Full and compare (this is the evidence
  that Lite is enough as a default).

Each run is a council session on the question **as originally asked**, against the repository at the
listed commit — the last first-parent `master` commit before the session date, so the outcome is not
in the tree (`git worktree add ../vnext-eval <sha>`) — without access to the session's own
`DECISION.md` or the log row. It costs one council run per case and mode, so ask before running the set.

## Cases

| # | Question as asked (log row) | Expected finding (pass = found and stated as such) | Repository at |
|---|---|---|---|
| E1 | A post-commit `ContinueParent` continuation re-enters admission without status ownership — state-level SubProcess (`subFlow.type "P"`) cannot run on the sync path (2026-09-22) | Premise invalid: a state starts only an `S` SubFlow (`WorkflowValidator.ValidateStateSubFlowType`); a `P` is started by `SubProcessTask`. Status `VOID`. (Originally found by the requester, not the council — the case tests whether the council finds it.) | `507dde25` |
| E2 | Move the oversized async-transition body from `InstanceJobRequestData` to an Aether arm-by-reference capability (2026-10-02) | Premise refuted: the body is nulled in the envelope when offloaded (`AsyncTransitionStrategy`), so there is no redundant second copy. | `aea25211` |
| E3 | Remove role-based authorization from every execution/read surface, with the gateway's `check-authorize` as the single decision point (2026-09-22) | The gateway covers none of the runtime's routes, so removal deletes a control; `authorize` was weaker than the gates it would replace. | `507dde25` |
| E4 | Redesign the `human-task` function: root-projection predicate on `EffectiveStateSubType`, 60 s cache, replacement index (2026-09-17) | `EffectiveStateSubType` has no reset writer, so phantom tasks list today; select by the instance's own state instead; the endpoint has no authentication (blocks the cache). | `f3856b8a` |
| E5 | Surface the workflow-level timeout on the state function as a countdown, with two new `InstanceJob` columns (2026-09-21) | A defect, not a gap: the subflow timeout override was armed but never fired; one shared effective-timeout resolver, no new columns. | `5dbc19a8` |

## Recording a run

Add one line per run, newest last. Score 1 when the expected finding is in the decision with its
evidence, ½ when it is raised only as an assumption or open question, 0 when missed.

| Date | Change under test | Mode | E1 | E2 | E3 | E4 | E5 | Notes |
|---|---|---|---|---|---|---|---|---|
