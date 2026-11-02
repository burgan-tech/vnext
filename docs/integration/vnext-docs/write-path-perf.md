---
product: vnext-docs
milestone: v0.0.102
status: pending
---

# Write-path performance changes — Handoff for vnext-docs

## Context

- vNext PR: https://github.com/burgan-tech/vnext/pull/TBD
- Milestone: v0.0.102

## What changed in the runtime

- **`functions/tasks` no longer lists a task while it is running.** The task journal row is written
  once, at completion, with its final `status` (`completed` | `faulted`), business status, payloads
  and timings. Rows with `waiting` / `busy` exist only for tasks journaled by older runtimes. Source of
  truth: `docs/runtime/instance-task-and-action-history.md` § One write per task.
- New operator configuration (documented for domain/platform teams, not for flow authors):
  HTTP task client pool dials on both hosts, `WorkflowExecution:Bookkeeping:AsynchronousCommit`,
  and the new default of `WorkflowExecution:SubItemTerminalLockRetry` (10 × 20 ms). Source of truth:
  `docs/runtime/task-invocation-routing.md`, `docs/runtime/event-publish-modes.md`, the three
  `vnext-meta/unreleased/*.json` fragments named in the PR.
- No change to any public API shape, event contract or component schema.

## What this product must do

- Update the task-history / monitoring page: the `status` values a client can observe from
  `functions/tasks` on a current runtime, and that an in-flight task is not listed.
- Add the configuration keys to the runtime configuration reference with their defaults and the
  "measure before enabling" note for `AsynchronousCommit`.

## Contract and examples

```json
// GET .../instances/{id}/functions/tasks — a row on a current runtime
{ "id": "…", "taskKey": "send-otp", "status": "completed", "businessStatus": "success",
  "startedAt": "…", "finishedAt": "…", "durationMs": 184.2 }
```

## Acceptance

- The docs portal no longer promises a `busy` row for a running task.
- Every new key appears in the configuration reference with the code default.

## Out of scope

- Flow-authoring guidance; nothing changes for component authors.
