---
product: vnext-ai-toolkit
milestone: v0.0.103
status: pending
---

# Python Task container runtime (type 23) — Handoff for vnext-ai-toolkit

## Context

- vNext PR: the python-runtime-wrapper PR of milestone v0.0.103 (branch `claude/python-runtime-wrapper`)
- Milestone: v0.0.103

## What changed in the runtime

PythonTask (type 23) references a handler in the domain's Python runtime image instead of carrying
code: config `handler`, `input`, `timeoutSeconds`. Source of truth: `docs/runtime/python-task.md`.

## What this product must do

- `component-task`: scaffold type 23 with `handler` / `input` / `timeoutSeconds`; never emit
  `script` or `executionMode`.
- Offer to scaffold the domain side: `handlers/<module>.py` with `@handler("name")`, a pinned
  `requirements.txt` and the `Dockerfile` `FROM ghcr.io/burgan-tech/vnext/python-runtime:<version>`.

## Contract and examples

```json
{ "type": "23", "config": { "handler": "sum_values", "input": { "values": [1, 2] } } }
```

## Acceptance

- A scaffolded type-23 task validates against vnext-schema with type 23 and publishes.

## Out of scope

- Building or pushing the domain image.
