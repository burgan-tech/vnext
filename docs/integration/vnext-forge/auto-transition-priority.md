---
product: vnext-forge
milestone: v0.0.102
status: pending
---

# Automatic transition priority (`transition.priority`) — Handoff for vnext-forge

## Context

- vNext PR: the auto-transition-priority PR of milestone v0.0.102
- Milestone: v0.0.102
- Issue: <https://github.com/burgan-tech/vnext/issues/1133>

## What changed in the runtime

An automatic transition (`triggerType: 1`) may declare an optional integer `priority` (minimum 1,
absent = 100). The runtime evaluates a state's automatic transitions by priority ascending, then
declaration order, with the DefaultAutoTransition (`triggerKind: 10`) always last. Source of truth:
vnext-schema `transition.priority` and the vnext-meta entry `features.engine.autoTransitionPriority`
(`schemaPath: workflow.attributes.states[].transitions[].priority`).

## What this product must do

- Show a `priority` number input on a state-level automatic transition that is not the
  DefaultAutoTransition; hide or disable it for manual, scheduled, event, shared and start
  transitions and for `triggerKind: 10`.
- Validate minimum 1 inline; leaving it empty means 100 (do not write `100` into the JSON).
- Optionally show the effective evaluation order of a state's automatic transitions, and warn when
  two of them share an effective priority while one declares it explicitly (the runtime's publish
  warning).

## Contract and examples

```json
{ "key": "to-high", "target": "high", "triggerType": 1, "priority": 10, "rule": { "...": "..." } }
```

Publish rejects `priority < 1` and `priority` on any transition other than a state-level automatic,
non-default one.

## Acceptance

- vnext-example `core/Workflows/auto-priority-lab/` opens, edits and saves without losing `priority`.
- The field cannot be set where the runtime rejects it.

## Out of scope

- Reordering the `transitions` array to reflect priority — declaration order still breaks ties.
