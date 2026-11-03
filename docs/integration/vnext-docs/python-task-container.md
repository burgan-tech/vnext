---
product: vnext-docs
milestone: v0.0.103
status: pending
---

# Python Task container runtime (type 23) — Handoff for vnext-docs

## Context

- vNext PR: the python-runtime-wrapper PR of milestone v0.0.103 (branch `claude/python-runtime-wrapper`)
- Milestone: v0.0.103

## What changed in the runtime

Python is no longer part of any vNext image. A domain enables the Helm `pythonRuntime` block and
ships its own image `FROM ghcr.io/burgan-tech/vnext/python-runtime:<version>` with its handlers and
pinned requirements. Orchestration calls `POST v1/invoke` on that container over Dapr (contract
v1). Source of truth: `docs/runtime/python-task.md` (runtime repo).

## What this product must do

- Replace the Python Task page: task fields (`handler`, `input`, `timeoutSeconds`), the mapping
  hook, the contract request/response and the error-code table.
- A domain-owner guide: write a handler (`@handler("name")`), build the image, enable
  `pythonRuntime` in Helm (image, resources, config), publish the task.
- A migration note for the removed `script` / `executionMode` fields (breaking change).

## Contract and examples

Use the example in `runtimes/python/example/` and the e2e lab in vnext-example
`labs/python-runtime/` (domain image + sidecar) as the worked example.

## Acceptance

- A reader can go from zero to a working Python task using only the docs page.

## Out of scope

- Internal Orchestration implementation details.
