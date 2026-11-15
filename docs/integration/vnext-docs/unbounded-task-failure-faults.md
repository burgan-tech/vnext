---
product: vnext-docs
milestone: v0.0.107
status: pending
---

# A task failure with no error boundary faults the transition — Handoff for vnext-docs

## Context

- vNext PR: burgan-tech/vnext — `fix(tasks): fault the transition when a task fails without an error boundary`
- Milestone: v0.0.107
- Issue: https://github.com/burgan-tech/vnext/issues/1257 (source: https://github.com/burgan-tech/vnext-client-sdk-core/issues/95)

## What changed in the runtime

- A task that answers with a business failure while **no** `errorBoundary` is declared on the task,
  its state or the workflow is now **unhandled**: the hook stops at that task, an incident is written
  (attributed to the task, `boundaryAction: null`) and the instance becomes **Faulted**. Before, the
  transition completed as if the task had succeeded, no incident was written and the tasks after it
  in the same hook were silently skipped.
- Automatic transitions no longer "handle" such a failure. To route an expected failure to another
  state, declare a boundary with `Rollback` / `Notify` and a `transition`.
- `Ignore` / `Log` now let the **remaining tasks of the hook run** (before, the rest of the hook was
  skipped). They still write no incident.
- A function or extension whose only failures were handled by `Ignore` / `Log` now succeeds.
- A FanOut item that fails with no boundary carries the inner task's own error code and response
  data, not `FanOut:ItemFailed`.

Source of truth: `vnext-meta` migrations `unbounded-task-failure-faults-transition` and
`ignore-log-boundary-runs-remaining-tasks`; runtime docs `docs/runtime/instance-incident-persistence.md`
and `docs/runtime/task-executors-and-invokers.md`.

## What this product must do

- `docs/how-to/error-handling.md` (and the `i18n/en` copy): add a "no boundary declared" row/section —
  the failure faults the instance with an unattributed-verdict incident; recommend `Ignore`/`Log` for
  tolerated failures and `Rollback`/`Notify` + `transition` for routing. The `Ignore` row ("devam et
  sonraki task'a") is now accurate; add that `Log` behaves the same.
- `docs/concepts/incidents.md`: state that a task failure with no boundary at any level records an
  incident with `boundaryAction: null`, like a declared boundary that matched no rule.
- `docs/components/tasks/fan-out.md` (and the `i18n/en` copy, row `FanOut:ItemFailed`): drop "typically
  when no error boundary applies"; an unbounded item failure now carries the inner task's code.
- Release blog for v0.0.107: name the behaviour change (the client reported about 20 tasks that start
  faulting) and the opt-out (declare `Ignore`).

## Contract and examples

Keep the old "continue" outcome for one task by declaring:

```json
"errorBoundary": { "onError": [ { "action": 3, "errorCodes": ["*"], "priority": 999 } ] }
```

Route an expected failure instead of faulting:

```json
"errorBoundary": { "onError": [ { "action": 4, "errorCodes": ["404"], "transition": "not-found" } ] }
```

## Acceptance

- The error-handling page answers "what happens when no boundary is declared" in one place.
- No page says that automatic transitions handle a failed task or that `Ignore` skips the remaining tasks.

## Out of scope

- The `updateData` two-layer schema validation (burgan-tech/vnext#1285).
