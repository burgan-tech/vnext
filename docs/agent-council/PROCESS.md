# Agent Council Process

## States

`DRAFT` -> `CONTEXT_READY` -> `COUNCIL_SELECTED` -> `INDEPENDENT_ANALYSIS` -> `DEBATE` -> `DECISION_PENDING` -> `CHAIR_APPROVAL` -> `APPROVED` -> `IMPLEMENTING` -> `REVIEWING` -> `VALIDATING` -> `COMPLETED`

Alternative outcomes: `CLARIFICATION_REQUIRED`, `EXPERIMENT_REQUIRED`, `REJECTED`, `BLOCKED`.

## Session Contract

Store each session under `sessions/<task-id>/`. `SESSION.md` is the manifest and active-state record. Artifacts are immutable in practice: revisions create a new version and reference `Supersedes`. Each artifact records Session ID, Artifact ID, Version, author actor ID, UTC timestamp, status, evidence reference, and supersession information.

The workflow/orchestrator is the only state owner. Record every transition with actor, UTC time, reason, and evidence reference. Approved artifacts require append-only storage or an equivalent immutable event log.

## Gates

| State | Entry gate | Exit evidence | Owner |
|---|---|---|---|
| `DRAFT` | Task exists | Problem, scope and owner | Facilitator |
| `CONTEXT_READY` | Task is complete | Criteria, NFRs, risk and unknowns | Facilitator |
| `COUNCIL_SELECTED` | Risk is classified | Roles, identities and separation-of-duties check | Facilitator |
| `INDEPENDENT_ANALYSIS` | Council selected | One proposal per required role before visibility | Workflow |
| `DEBATE` | Proposals locked | Evidence-based objections, maximum two rounds | Workflow |
| `DECISION_PENDING` | Debate closed | Matrix, dissent, risks, verification and rollback | Facilitator |
| `CHAIR_APPROVAL` | Chair required | Valid Chair decision and conditions | Configured Chair |
| `APPROVED` | Gates pass | Approved decision and implementation plan | Workflow |
| `IMPLEMENTING` | Plan and rollback ready | Change and execution evidence | Implementer |
| `REVIEWING` | Implementation complete | Independent review | Reviewer |
| `VALIDATING` | Review passes | Tests, NFR, observability and rollback evidence | Test & Evidence |
| `COMPLETED` | DoD passes | Closure and remaining-risk record | Workflow |

`CLARIFICATION_REQUIRED`, `EXPERIMENT_REQUIRED`, `REJECTED`, `BLOCKED`, and `COMPLETED` are terminal for that session version. Reopening creates a linked new version.

## Debate

Proposal owners respond with `ACCEPT`, `COUNTER`, `DEFER_TO_EXPERIMENT`, or `ESCALATE`. Stop after two rounds or when no new evidence is produced.

## Hard Stops

Missing security, data ownership, acceptance, rollback, or performance evidence cannot be repaired by a score. `APPROVE_WITH_CONDITIONS` cannot enter implementation until every mandatory condition is owned, dated, evidenced, and `Satisfied` or authorized `Waived`.
