---
product: vnext-docs
milestone: v0.0.102
status: pending
---

# Automatic transition priority (`transition.priority`) — Handoff for vnext-docs

## Context

- vNext PR: the auto-transition-priority PR of milestone v0.0.102
- Milestone: v0.0.102
- Issue: <https://github.com/burgan-tech/vnext/issues/1133>

## What changed in the runtime

A state's automatic transitions are evaluated in a defined order, and a flow author can set it with
an optional integer `priority` on each automatic transition. Source of truth when this page and the
engine disagree: `State.AutoTransitions` (`src/BBT.Workflow.Domain`), the "Automatic transition
order" section of `docs/architecture/workflow-execution-pipeline.md`, and the vnext-meta entry
`features.engine.autoTransitionPriority`.

- Order: non-default automatic transitions first, by `priority` ascending, then declaration order;
  the DefaultAutoTransition (`triggerKind: 10`) is always evaluated last. The first satisfied rule wins.
- `priority` is an integer, minimum 1; lower is evaluated first; absent counts as 100.
- Evaluation is sequential and stops at the first satisfied rule; a rule that throws fails the step
  and later rules are not evaluated.
- Publish errors: `priority` below 1; `priority` on a manual, shared or start transition; `priority`
  on the DefaultAutoTransition. Publish warning: two automatic transitions of one state share an
  effective priority while at least one of them declares it explicitly.
- Order change for existing flows: before this release an automatic transition with no `triggerKind`
  was evaluated before one with an explicit `triggerKind: 0`. Both now keep declaration order.

## What this product must do

- Document `priority` on the automatic-transition page (field table: type, minimum, default 100,
  allowed only on state-level automatic non-default transitions).
- Describe the evaluation order and the "first satisfied wins" rule, with the default transition last.
- Add the publish errors and the tie warning to the validation page.
- Note the order change for flows that mixed an absent and an explicit `triggerKind: 0`.

## Contract and examples

```json
"transitions": [
  { "key": "to-low",  "target": "low",  "triggerType": 1, "priority": 30, "rule": { "...": "..." } },
  { "key": "to-high", "target": "high", "triggerType": 1, "priority": 10, "rule": { "...": "..." } },
  { "key": "to-fallback", "target": "fallback", "triggerType": 1, "triggerKind": 10 }
]
```

`to-high` is evaluated first even though it is declared second; `to-fallback` is taken only when
neither rule is satisfied.

## Acceptance

- The automatic-transition page states the order and the default of 100, and shows the example above.
- The working example is vnext-example `core/Workflows/auto-priority-lab/`.

## Out of scope

- Parallel evaluation of automatic rules (not offered).
- Priority on manual, scheduled, event or shared transitions.
