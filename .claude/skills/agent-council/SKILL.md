---
name: agent-council
description: "Runs the evidence-based Agent Council for non-trivial vNext decisions (architecture, technology, cross-service, data, security, reliability, performance) and logs the decision. Triggers: \"council\", \"karar verelim\", \"eklemeli miyim\", \"mimari karar\", or any question whose answer is a recommendation."
---

# Agent Council

Planning and decision process only. It **never** implements code.

Authority documents live in `docs/agent-council/`: `CHARTER.md`, `PROCESS.md`,
`COUNCIL-SELECTION.md`, `GOVERNANCE.md`, `CHAIR.md`, `roles/*.md`, `templates/*.md`.
Read `PROCESS.md` and the templates before producing artifacts — this skill is the
procedure, those files are the contract.

## Trigger, scope and mandatory disclosure

When the council applies (trigger phrases, the question *shape* that needs no phrase, the cases it
does not apply to) and the obligation to open every qualifying answer with its council status are
defined once, in the always-on rule
[`.claude/rules/agent-council-plan-mode.md`](../../rules/agent-council-plan-mode.md) § Trigger and
§ Mandatory disclosure. Read it there; it is not restated here. If unsure whether a question
qualifies, say in one sentence that it looks council-shaped and ask.

## Workflow

### 1. CONTEXT_READY — write the task record

Create `ai-docs/agent-council/sessions/<task-id>/TASK.md` (git-ignored local scratch) from
`templates/TASK.md`. `<task-id>` is `YYYY-MM-DD-<slug>`.

Classify risk (`Low | Medium | High | Critical`). Separate **verified facts**
(repository evidence) from **assumptions** and **open questions** — this split
decides the verdict later.

Also create `SESSION.md` from `templates/SESSION.md` and log the first transition.
The session manifest is the state owner; every state change gets a row with actor,
UTC time, reason and evidence.

### 2. COUNCIL_SELECTED — pick the smallest sufficient council

Apply [`docs/agent-council/COUNCIL-SELECTION.md`](../../../docs/agent-council/COUNCIL-SELECTION.md)
— the core roles, the conditional-role triggers and the size per risk level live there.

**Record which blind spot each added role closes, and why each omitted role was
omitted.** Do not select every role by default.

**Include Devil's Advocate whenever the expected answer is "no".** Its job is then to
argue the *for* case with evidence. A council where nobody argued the losing side did
not happen.

Separation of duties: the facilitator who writes `DECISION.md` must not also own a
proposal. Record the check in `SESSION.md`.

### 3. INDEPENDENT_ANALYSIS — parallel proposals, no cross-visibility

Gather the evidence pack **first** (Explore agents, `git log`/`git show --stat` for
cost precedents, the relevant `/docs` pages). Every role gets the *same* pack.

Then launch one `Agent` per role, **all in a single message so they run in parallel**.
Each prompt carries: the role file from `docs/agent-council/roles/`, `TASK.md`, the
shared evidence pack, and `templates/PROPOSAL.md`.

Each agent returns a proposal. They must not see each other's output — that is what
makes the analysis independent.

Write each to `PROPOSAL-<nn>-<role-slug>.md`.

### 4. DEBATE — two rounds, via SendMessage

**Round 1 (objections).** `SendMessage` each role agent the other proposals. Each
returns objections in `templates/OBJECTION.md` shape: challenged claim, evidence,
impact, requested revision, severity. An objection without evidence or new information
is not an objection.

**Round 2 (responses).** `SendMessage` each proposal owner the objections targeting
its proposal. Each responds `ACCEPT | COUNTER | DEFER_TO_EXPERIMENT | ESCALATE` with
rationale. Record responses in the same objection file.

Reuse the same agents through `SendMessage` — their context stays intact and the role
stays consistent across waves. Stop after two rounds or as soon as no new evidence is
being produced.

### 5. DECISION_PENDING — record the decision

Write `DECISION.md` from `templates/DECISION.md`: decision, selected proposal,
rationale, scored matrix, rejected alternatives, **dissent**, accepted risks,
verification checklist, rollback.

**An empty dissent table means the debate did not really run.** Go back to step 4.

Verdict: `APPROVED` · `EXPERIMENT_REQUIRED` · `CLARIFICATION_REQUIRED` · `BLOCKED` ·
`REJECTED`.

**Append the decision to the log.** Add one row to `docs/agent-council/sessions/README.md`
in the same shape as the existing rows:
`| YYYY-MM-DD | [<task-id>](../../../ai-docs/agent-council/sessions/<task-id>/DECISION.md) | topic | risk | verdict | selected approach | follow-up |`.
Keep each free-text cell to ~300 characters (decision + status + one-sentence rationale); the
detail belongs in the local session folder.
The log is the team's decision history; a session without a row is not recorded. If a later
session changes the verdict, update the row and point `Follow-up` at the superseding session.

### 6. Chair

`CHAIR.md` gates service-boundary and data-ownership changes, new infrastructure,
auth/secret changes, breaking cross-service contracts, production migrations and
high-blast-radius changes.

**If `CHAIR.md` has no configured identity, the decision closes as `Awaiting Chair`**
and "name the Chair" is recorded as an open condition in `SESSION.md`. Do not treat an
unconfigured Chair as an absent gate.

## Hard gates

- Missing workload, security, data-ownership, rollback or acceptance evidence is never
  an approval. It is `CLARIFICATION_REQUIRED`, `EXPERIMENT_REQUIRED` or `BLOCKED`.
- A hard constraint cannot be outvoted or outscored.
- Never claim performance, recovery, security or compatibility without a reproducible
  test, a measurement, repository evidence, or explicit human approval. An unmeasured
  performance claim is `EXPERIMENT_REQUIRED`, not a rationale.
- Where the requester's actual need was never established, the verdict cannot be a
  clean `REJECTED` — it is at least `CLARIFICATION_REQUIRED`.
- The implementer cannot be the final reviewer of the same change.

## Boundary — this is not implementation approval

The council may read any repository file and write **only** under
`ai-docs/agent-council/sessions/` (git-ignored) — the session folder, plus one appended row in
`docs/agent-council/sessions/README.md` (the decision log). It must not touch production source, tests,
migrations, deployment manifests, generated contracts, `etc/`, `vnext-meta/` or
project configuration.

After a decision, implementation needs a *separate* task and explicit user direction.

Do not start infrastructure or run expensive integration tests during council
planning without explicit user approval (see `docs/testing/integration-testing.md` §1).

## Repository-specific checks

- Read `docs/agent-onboarding.md` before planning a vNext change.
- Code is the source of truth for current behavior; `LifecycleOrder.cs` and
  `PipelineExecutionProfile.cs` win over any doc.
- Validation plans must follow the repo's Aether, outbox, multi-schema, logging and
  Result-pattern policies, and the integration-test policy in `docs/testing/integration-testing.md`.
- Task/component schemas live in the **external** `@burgan-tech/vnext-schema` package.
  Any decision adding a component type must record that coordination cost.
