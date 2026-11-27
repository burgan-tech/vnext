# Agent Council

The council is vNext's evidence-backed decision step for non-trivial engineering questions. It plans and
decides; it never implements. This page is the **single contract**: modes, roles, verdicts, conditions,
risk and the log format. Everything else points here.

| What | Where |
|---|---|
| When it fires, mandatory disclosure, plan-mode boundary | [`.claude/rules/agent-council-plan-mode.md`](../../.claude/rules/agent-council-plan-mode.md) |
| How to run it (procedure) | [`agent-council` skill](../../.claude/skills/agent-council/SKILL.md) |
| Role briefs | [roles.md](roles.md) |
| Chair identity, mandatory-approval list, standing precedents | [CHAIR.md](CHAIR.md) |
| Artifacts | [templates/DECISION.md](templates/DECISION.md), [templates/PROPOSAL.md](templates/PROPOSAL.md) |
| Decision history (the only committed record) | [sessions/README.md](sessions/README.md) |
| Regression cases for the council itself | [evals.md](evals.md) |
| CI variant (one agent, JSON verdict) | [`.github/ai/council.md`](../../.github/ai/council.md), [pipeline §3](../governance/ai-engineering-pipeline.md#3-change-classification-and-guard-matrix) |

Session folders live in `ai-docs/agent-council/sessions/<YYYY-MM-DD-slug>/` (git-ignored, local to the
machine that ran the session).

## Principles

1. Evidence before seniority or majority. Code beats docs (`LifecycleOrder.cs`,
   `PipelineExecutionProfile.cs`, `PipelineProfileResolver.cs`); read `docs/agent-onboarding.md` first.
2. Security and data integrity are hard constraints; no score or vote overrides one.
3. Verified facts (file:line) are kept apart from assumptions — that split drives the verdict.
4. Someone argues the losing side. Dissent is recorded, never averaged away.
5. The council **moves work forward**: every non-approval names the next concrete action, its owner
   and what would change the verdict.

## Modes

| | **Lite** (default) | **Full** |
|---|---|---|
| When | Every council question unless Full applies | Critical risk, an explicit "full council" request, two or more triggered conditional roles, or a Lite session escalated to Full |
| Roles | Solution Architect, Pragmatic Engineer, Devil's Advocate + at most one conditional role | Smallest sufficient set from the table below (High 4–7, Critical all applicable + a human expert) |
| Debate | One objection round | Objections, then responses (`ACCEPT`/`COUNTER`/`DEFER_TO_EXPERIMENT`/`ESCALATE`) |
| Output | One `DECISION.md` (+ one `PROPOSAL-*.md` per role) | Same |

Test & Evidence duties (claim → evidence traceability) are owned by the facilitator in Lite and by the
Test & Evidence role in Full.

**Escalating Lite → Full.** Stop and rerun as Full when framing finds Critical risk, when two
conditional roles trigger, or when a hard constraint is still contested after the objection round.
Record `Amendment YYYY-MM-DD: escalated to Full` in the same `DECISION.md`; the session keeps its slug.

**CI council.** One agent plays every role in one run with one objection round. Its core roles are
Solution Architect, Pragmatic Engineer and Test & Evidence — the only ones the trusted script enforces
(`CORE_ROLES` in `scripts/ai/lib/council.mjs`). The prompt (`.github/ai/council.md`) always adds
Devil's Advocate and every triggered conditional role, with no cap; the script does not verify those.

## Roles and selection

Core: Solution Architect, Pragmatic Engineer, Devil's Advocate (Lite) · the same plus Test & Evidence (Full) · CI core: see above.

| Trigger | Add |
|---|---|
| Authentication, authorization, PII, secrets | Security Guardian (mandatory — security work is never approved without it) |
| Data model, migration, consistency, transactions | Data Architect |
| Latency, throughput, allocation, cache, concurrency | Performance Engineer (Full) |
| Deployment, recovery, production operations | Reliability Engineer (Full) |

Record which blind spot each added role closes. Do not add every role by default. The facilitator who
writes `DECISION.md` owns no proposal; the implementer of a change is never its final reviewer.

## Risk

Risk uses the impact triggers of the [AI pipeline §3](../governance/ai-engineering-pipeline.md#3-change-classification-and-guard-matrix).
The CI **labels** are coarser than this table and must not be mapped onto it: `ai:risk-high` means any
high-impact change, core-process-only ones included (Medium here, and CI sends them to development
without a council), and `ai:risk-critical` means a breaking (major) change.

| Risk | Holds when | Chair |
|---|---|---|
| Low | Local refactor, isolated fix or test | Only if on the [mandatory-approval list](CHAIR.md#mandatory-approval) |
| Medium | Touches a core process (pipeline, transitions, subflows, locking, state function) **without** any High trigger | Only if on the mandatory-approval list |
| High | Auth/secret/PII · persisted shape or migration · public API, event contract or `ResponseShapeVersion` · cross-service consistency (outbox/inbox/relays) · a companion contract change in another product | Required |
| Critical | A High trigger that is also irreversible in production (a migration that cannot be rolled back), crosses a trust boundary between services or tenants, or breaks a published contract | Required, double check |

"It touches a core-process file" alone is Medium, not High.

## Verdicts

One vocabulary, also enforced by `scripts/ai/lib/council.mjs` (`VERDICTS`) and checked by
`scripts/check-ai-guidance.sh`:

<!-- council-verdicts:begin -->
`APPROVED` · `APPROVE_WITH_CONDITIONS` · `EXPERIMENT_REQUIRED` · `CLARIFICATION_REQUIRED` · `BLOCKED` · `REJECTED`
<!-- council-verdicts:end -->

| Verdict | When | Next action it must name |
|---|---|---|
| `APPROVED` | Sound, hard constraints hold, no `blocking` condition | Implementation task |
| `APPROVE_WITH_CONDITIONS` | Sound, but at least one `blocking` condition | Owner + due date of each blocking condition |
| `EXPERIMENT_REQUIRED` | A performance, recovery or compatibility claim needs a measurement | A runnable experiment: hypothesis, measurement, stop/go threshold, cost |
| `CLARIFICATION_REQUIRED` | Workload, data ownership, security, rollback or acceptance evidence missing; or the requester's need was never established | The questions, addressed to a named person |
| `BLOCKED` | A hard constraint is violated | The constraint and what would unblock it |
| `REJECTED` | The change should not be made | — |

Session status, beside the verdict: `Decided` (closed, no Chair needed or the Chair has ruled),
`Awaiting Chair` (Chair approval required and not yet given), `VOID` (the premise turned out false —
nothing to decide; a VOID session has **no verdict**, its verdict cell is `— (VOID)`), `SUPERSEDED`
(a later session replaced it).

**One headline verdict.** It is the verdict of the scope that can move **now**. Other parts of the
question go in the item table of `DECISION.md` with their own verdicts and become follow-ups; a log row
never carries a list of verdicts.

**Hard rules.** Missing evidence is never an approval. An unmeasured performance claim is
`EXPERIMENT_REQUIRED`, not a rationale. A claim of performance, recovery, security or compatibility needs
a reproducible test, a measurement, repository evidence or explicit human approval.

## Conditions

Every condition has an id, an owner and a class. **Only `blocking` conditions hold implementation back.**

| Class | Meaning | Blocks |
|---|---|---|
| `blocking` | Must be met before implementation starts: an owner decision, a companion release, an experiment result | Start |
| `in-PR` | Proven inside the implementation PR: tests, a measurement on the branch, docs, deprecation entry | Merge (listed in the PR body, checked by the human reviewer — no automated gate reads them) |
| `follow-up` | Separate issue with owner and due date | Nothing |

A `blocking` condition closes as `Satisfied` (with evidence) or `Waived` (by the Chair, or by the
requester when no Chair gate applies).

## Experiments

`EXPERIMENT_REQUIRED` is the next task, not a parking state. The experiment may run on a spike branch
right away; starting infrastructure or expensive integration runs still needs the user's go-ahead
(`docs/testing/integration-testing.md` §1). Its result re-opens the same session as an amendment.

## Scoring (Full, optional in Lite)

Hard constraints pass first. Score each criterion 0–5, `Total = sum(score / 5 * weight)`; ties go to
reversibility, then simplicity.

## Chair

Identity, the mandatory-approval list and the standing precedents are in [CHAIR.md](CHAIR.md). A High or
Critical decision, and any decision on the mandatory-approval list, closes as `Awaiting Chair` until the
Chair answers. **Exception — the CI council:** its `APPROVED` authorizes development only, never merge;
the human review of the PR is the gate, so its log rows carry no `Awaiting Chair`
([pipeline §3](../governance/ai-engineering-pipeline.md#3-change-classification-and-guard-matrix)). To keep the queue short, `DECISION.md` ends
with **at most five yes/no Chair questions, each with a recommended answer**, and the skill's *chair
queue* mode collects every open one in a single pass.

## Amendments

A later decision on the same session is recorded, not re-told:

- `Chair YYYY-MM-DD: <verdict>` — the Chair's ruling (also when the requester reports it).
- `Amendment YYYY-MM-DD: <what changed>` — a requester direction within the decided scope.
- A change of scope is a new Lite session whose row links the old one.

Append the amendment to the session's `DECISION.md` and to the log row's Verdict or Follow-up cell.

## Boundary

What a session may read and write, and why a decision is not implementation approval: the
[plan-mode rule](../../.claude/rules/agent-council-plan-mode.md#plan-mode-boundary). A decision that
adds a component type records the coordination cost with `@burgan-tech/vnext-schema`, `vnext-meta` and
the Helm chart.
