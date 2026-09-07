# Agent Council

Agent Council is vNext's evidence-backed planning gate for non-trivial engineering decisions. It runs in plan mode only: the council may inspect the repository and produce decision artifacts, but it does not implement code or change runtime behavior.

## Use It For

Use the council for pipeline, transition, subflow, cross-service, contract, data, security, reliability, performance, deployment, and major architectural changes. Small isolated fixes may use the lightweight path in `.claude/rules/agent-council-plan-mode.md`.

## Start A Session

1. Copy `templates/TASK.md` and `templates/SESSION.md` into `sessions/<task-id>/`.
2. Read `project/PROJECT-CONTEXT.md`, `project/ARCHITECTURE-PRINCIPLES.md`, and `project/DEFINITION-OF-DONE.md`.
3. Select the smallest risk-appropriate council using `COUNCIL-SELECTION.md`.
4. Collect independent proposals, then run no more than two objection rounds.
5. Record `DECISION.md`; use `EXPERIMENT_REQUIRED`, `CLARIFICATION_REQUIRED`, or `BLOCKED` when evidence is incomplete.
6. Do not create `IMPLEMENTATION-PLAN.md` as executable work until the decision and required Chair approval are complete.
7. After approval, obtain explicit user direction before making source changes.

## Required Artifacts

| Artifact | Purpose |
|---|---|
| `TASK.md` | Problem, scope, acceptance criteria, NFRs and access boundary |
| `SESSION.md` | State owner, artifact versions, transitions and open conditions |
| `PROPOSAL.md` | Independent solution proposal and verification plan |
| `OBJECTION.md` | Evidence-based challenge and response |
| `DECISION.md` | Selection, scoring, dissent, risks and approval |
| `IMPLEMENTATION-PLAN.md` | Approved implementation and rollback plan |
| `REVIEW.md` | Independent implementation review and validation evidence |

## vNext Sources Of Truth

Read `docs/agent-onboarding.md` first. Current runtime behavior is defined by code, especially `LifecycleOrder.cs`, `PipelineExecutionProfile.cs`, and `PipelineProfileResolver.cs`. Keep council claims aligned with current `/docs`, Aether rules, outbox delivery, multi-schema, Result pattern, and integration-test guidance.
