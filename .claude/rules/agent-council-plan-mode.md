# Agent Council Plan Mode

## Scope

This rule applies to architecture, technology-selection, cross-service, data-model,
security, reliability, performance, and other non-trivial engineering decisions in
this repository.

The council is a **planning and decision process only**. It must not implement code,
modify runtime behavior, create migrations, start infrastructure, commit changes,
push branches, or open pull requests while the council session is active.

## Trigger

The runnable procedure is the **`agent-council` skill** (`.claude/skills/agent-council/`).
Invoke it through the `Skill` tool when either signal below fires. This rule states
*when* and *why*; the skill states *how*.

**Phrases**: "council", "council işlet", "karar verelim", "hangisini seçelim",
"eklemeli miyim", "eklemeli miyiz", "yapmalı mıyız", "should we add", "should we
build", "mimari karar", "hangi yaklaşım".

**Shape** — no phrase required. The question's honest answer is a *recommendation*
rather than a fact, and the subject is a new component/task type, service or public
contract; moving a responsibility between services; adopting or dropping a technology,
broker or infrastructure dependency; a data-model, schema or consistency change; an
auth/secret/PII boundary; a performance claim that would justify a change; or a
deployment/recovery change.

A request relayed from another team is a **strong** trigger, not a weak one: an outside
party already holds a position that nobody has argued against.

**Does not apply to**: single-file or single-method fixes, renames, doc edits, test
additions, purely informational questions, or carrying out a decision the user has
already made. Answer those directly. If unsure, ask in one sentence — do not silently skip.

## Mandatory disclosure

An answer to a qualifying question **must open by stating council status**: that the
council ran (linking the session), or that it was skipped and why. Answering a
council-shaped question silently from a single perspective is a violation of this rule,
not a shortcut. The most common failure is producing a well-researched answer in which
nobody argued the opposing side.

## Required flow

1. Create or update a task record with the problem, scope, acceptance criteria, NFRs,
   constraints, risk, affected components, known facts, assumptions, and open questions.
2. Select the smallest council that covers the risk:
   - Always: Solution Architect, Pragmatic Engineer, Test & Evidence Agent.
   - Security/auth/PII/secrets: Security Guardian.
   - Data/schema/transaction/consistency: Data Architect.
   - Performance/latency/throughput/concurrency: Performance Engineer.
   - Deployment/recovery/production operations: Reliability Engineer.
   - Cross-service or High/Critical risk: Devil's Advocate.
3. Collect independent proposals before exposing one proposal to another agent.
4. Run at most two evidence-based objection rounds. An objection must identify the
   claim, evidence, impact, requested revision, and response.
5. Record a decision containing the selected approach, alternatives, dissent, risks,
   verification plan, rollback plan, and unresolved conditions.
6. Mark the result as `APPROVED`, `EXPERIMENT_REQUIRED`, `CLARIFICATION_REQUIRED`,
   `BLOCKED`, or `REJECTED`. High/Critical decisions and Chair-gated decisions remain
   blocked until the configured Chair approves them.

## Hard gates

- Missing workload, security, data ownership, rollback, or acceptance evidence is not
  an approval; it is `CLARIFICATION_REQUIRED`, `EXPERIMENT_REQUIRED`, or `BLOCKED`.
- Hard constraints cannot be overridden by a score or majority vote.
- `APPROVE_WITH_CONDITIONS` cannot authorize implementation until every mandatory
  condition has an owner, due date, evidence reference, and `Satisfied` or `Waived`
  status from an authorized approver.
- The implementer cannot be the final reviewer of the same change.
- Do not claim performance, recovery, security, or compatibility without a reproducible
  test, measurement, repository evidence, or explicit human approval.

## Plan-mode boundary

During this process, the agent may inspect repository files and produce planning
artifacts under `docs/agent-council/`, `docs/superpowers/`, or a user-specified planning location. The agent
must not edit production source, tests, migrations, deployment manifests, generated
contracts, or project configuration as part of the council decision.

The council output is not implementation approval by itself. After approval, create a
separate implementation task and obtain explicit user direction before making code
changes. Keep the decision, dissent, evidence, and conditions linked to that task.

## Repository-specific checks

- Read `docs/agent-onboarding.md` before planning a vNext change.
- Use code as the source of truth when current behavior matters.
- Keep runtime and workflow claims aligned with `LifecycleOrder.cs`,
  `PipelineExecutionProfile.cs`, and the relevant current `/docs` page.
- Follow the repository's Aether, outbox, multi-schema, logging, Result-pattern,
  testing, and integration-test policies when defining the validation plan.
- Do not start infrastructure or expensive integration tests during council planning
  without explicit user approval.

## Required plan artifact

At minimum, the plan must link these sections or files:

- Task/context and acceptance criteria
- Council composition and separation-of-duties check
- Independent proposals
- Objections and responses
- Decision matrix and selected approach
- Dissent and accepted risks
- Experiment/test/measurement plan
- Rollback and deployment considerations
- Chair decision when required