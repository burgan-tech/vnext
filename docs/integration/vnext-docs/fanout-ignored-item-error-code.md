---
product: vnext-docs
milestone: v0.0.104
status: pending
---

# FanOut ignored item keeps the inner error — Handoff for vnext-docs

## Context

- vNext PR: the FanOut ignored-item error-code PR of milestone v0.0.104
- Milestone: v0.0.104
- Issue: https://github.com/burgan-tech/vnext/issues/1176

## What changed in the runtime

A FanOut item whose own `errorBoundary` resolves the inner task's failure to a non-blocking action
(`Ignore` = 3, `Log`) now reports what the inner task answered on its `FanOutItemResult`:

- `ErrorCode`: the normalized task code, `Task:{taskType}:{taskKey}:{status}` — for example
  `Task:DirectTrigger:notify-subprocesses-finalize:409`.
- `ErrorMessage`: the inner task's own message.
- `Data`: the inner task's response body, when it returned one.

Up to v0.0.103 such an item always arrived as `FanOut:ItemFailed` with the message
`FanOut item <key> failed.` and `Data = null`. Failures handled by a blocking action (`Abort`,
`Rollback`, …) or by no boundary at all are unchanged.

Source of truth when this page and the engine disagree: `FanOutTaskExecutor.MapEngineOutcome`
(`src/BBT.Workflow.Application/Tasks/Executors/FanOut/`) and
[docs/domain/fan-out-task.md](../../domain/fan-out-task.md) § Error codes and branching on partial
failure.

## What this product must do

- `docs/components/tasks/fan-out.md`, error-code table: narrow the `FanOut:ItemFailed` row — it is the
  fallback only when the inner task produced no code; an `Ignore`/`Log`-handled item carries the
  inner task's normalized code.
- Same page, `errorBoundary` row of the config table: add one sentence that an item whose failure is
  ignored keeps its inner code, message and data, so an `OutputHandler` can branch on the status
  (for example treat `:409` / `:404` as an expected refusal).
- Add a short migration note: an `OutputHandler` that matched `FanOut:ItemFailed` to detect ignored
  refusals should match the inner status code instead.

## Contract and examples

Item boundary that ignores client errors:

```json
"errorBoundary": { "onError": [ { "action": 3, "errorCodes": ["400", "404", "409"], "priority": 1 } ] }
```

The item whose target answers `409` reaches `OutputHandler` as:

```
IsSuccess    = false
ErrorCode    = "Task:DirectTrigger:<taskKey>:409"
ErrorMessage = <the inner task's message>
```

## Acceptance

- The fan-out page no longer says an ignored item becomes `FanOut:ItemFailed`.
- The example matches vnext-example `fan-out-config-matrix`,
  `PerItemErrorBoundary_Ignore_KeepsTheInnerTasksErrorCodeAndMessage`.

## Out of scope

- No new field on `FanOutItemResult`; the record is unchanged.
- The no-boundary path (no task, state or global boundary) still reports `FanOut:ItemFailed` with the
  inner message and data; do not document it as changed.
