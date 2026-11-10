---
product: vnext-forge
milestone: v0.0.103
status: pending
---

# Python Task container runtime (type 23) — Handoff for vnext-forge

## Context

- vNext PR: the python-runtime-wrapper PR of milestone v0.0.103 (branch `claude/python-runtime-wrapper`)
- Milestone: v0.0.103
- Schema: vnext-schema `feature/python-task-handler` (type 23)

## What changed in the runtime

PythonTask (`type: "23"`) no longer carries Python code. It names a **handler** registered in the
domain's own Python runtime container and the runtime calls it from Orchestration over Dapr. The
config is closed: `handler`, `input`, `timeoutSeconds`; the old `script` and `executionMode` fields
are rejected at publish. Source of truth: `docs/runtime/python-task.md` and the vnext-meta fragment
`python-runtime-container`.

## What this product must do

- Task designer for type 23: a required `handler` text field (pattern
  `^[A-Za-z_][A-Za-z0-9_.-]{0,127}$`), an optional JSON editor for `input`, a `timeoutSeconds`
  number (1–50, empty = 30).
- Remove the script editor and the `executionMode` selector for type 23; flag existing type-23 tasks
  that still carry `script` as needing migration.
- The mapping editor stays the standard `IMapping` (InputHandler / OutputHandler); the snippet
  `((PythonTask)task).SetInput(...)` is the input hook.
- Quick Run: a failed call shows `StatusCode` and `ErrorMessage` (`"<code>: <message>"`).

## Contract and examples

```json
{ "type": "23", "config": { "handler": "risk_score", "input": { "income": 85000 }, "timeoutSeconds": 20 } }
```

Failure codes: 400 `bad_request` / `unsupported_contract`, 404 `unknown_handler`, 413
`request_too_large`, 422 `handler_error` / `invalid_output` / `output_too_large`, 504 `timeout`;
503 when the domain has no runtime, 408 when the runtime does not answer.

## Acceptance

- A type-23 task authored in Forge publishes on a runtime of v0.0.103.
- A task with `script` is shown as invalid before publish.

## Out of scope

- Editing or deploying the handler code: it lives in the domain's runtime image, not in Forge.
