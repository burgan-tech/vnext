---
name: agent-council
description: Runs the evidence-based Agent Council decision process for non-trivial vNext decisions — architecture, technology selection, cross-service changes, data model, security, reliability, and performance. Produces independent role proposals, two objection rounds, and a recorded decision with dissent under ai-docs/agent-council/sessions/ (local, git-ignored) and one row in the committed log docs/agent-council/sessions/README.md. Use when the user says "council", "council işlet", "karar verelim", "hangisini seçelim", "eklemeli miyim", "should we add", "mimari karar", "bunu yapmalı mıyız", or asks any design/technology/cross-service question whose answer will be a recommendation.
---

# Agent Council

Planning and decision process only. It **never** implements code.

Authority documents live in `docs/agent-council/`: `CHARTER.md`, `PROCESS.md`,
`COUNCIL-SELECTION.md`, `GOVERNANCE.md`, `CHAIR.md`, `roles/*.md`, `templates/*.md`.
Read `PROCESS.md` and the templates before producing artifacts — this skill is the
procedure, those files are the contract.

## Trigger

Activate on either signal.

**Phrases**: "council", "council işlet", "karar verelim", "hangisini seçelim",
"eklemeli miyim", "eklemeli miyiz", "yapmalı mıyız", "should we add", "should we
build", "mimari karar", "hangi yaklaşım".

**Shape** — no trigger phrase needed. Activate when the user asks a question whose
honest answer is a *recommendation* rather than a fact, and the subject is one of:

- a new component type, task type, service, endpoint family or public contract
- moving a responsibility between services (Orchestration / Execution / Monitor / workers)
- adopting or dropping a technology, broker, library or infrastructure dependency
- a data model, schema, migration or consistency change
- an authentication, authorization, secret or PII boundary
- a performance/latency/throughput claim that would justify a change
- a deployment, recovery or production-operations change

A request arriving from another team ("ekipler istedi", "product asked for") is a
**strong** signal, not a weak one: it means an outside party already holds a position
that has not been argued against.

## When this skill does NOT apply

Do not activate for: a single-file or single-method fix, a rename, a typo, a
documentation edit, a test addition, a purely informational question ("how does X
work?", "where is Y?"), or a decision the user has already made and is asking you to
carry out. In those cases answer directly.

If unsure, say in one sentence that the question looks council-shaped and ask —
do not silently skip.

## Mandatory disclosure

Any answer to a qualifying question **must open by stating council status**: either
that the council ran (with a link to the session), or that it was skipped and why.
Silently answering a council-shaped question from a single perspective is a rule
violation, not a shortcut.

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

Per `COUNCIL-SELECTION.md`:

| Trigger | Role |
|---|---|
| Always | Solution Architect, Pragmatic Engineer, Test & Evidence |
| Auth, authorization, PII, secrets | Security Guardian |
| Data model, migration, consistency, transaction | Data Architect |
| Latency, throughput, allocation, cache, concurrency | Performance Engineer |
| Deployment, recovery, production operations | Reliability Engineer |
| Cross-service, or High/Critical risk | Devil's Advocate |

Size: Low 3 · Medium 3–5 · High 4–7 · Critical all applicable + a named human expert.

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
(`| date | [task-id](<task-id>/DECISION.md) | topic | risk | verdict | selected approach | follow-up |`).
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
planning without explicit user approval (see `CLAUDE.local.md`).

## Repository-specific checks

- Read `docs/agent-onboarding.md` before planning a vNext change.
- Code is the source of truth for current behavior; `LifecycleOrder.cs` and
  `PipelineExecutionProfile.cs` win over any doc.
- Validation plans must follow the repo's Aether, outbox, multi-schema, logging and
  Result-pattern policies, and the integration-test policy in `CLAUDE.local.md`.
- Task/component schemas live in the **external** `@burgan-tech/vnext-schema` package.
  Any decision adding a component type must record that coordination cost.
