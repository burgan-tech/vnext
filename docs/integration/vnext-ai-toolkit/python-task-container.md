---
product: vnext-ai-toolkit
milestone: v0.0.103
status: done
---

# Python Task container runtime (type 23) — Handoff for vnext-ai-toolkit

## Context

- vNext PR: the python-runtime-wrapper PR of milestone v0.0.103 (branch `claude/python-runtime-wrapper`)
- Milestone: v0.0.103
- Sibling PR: https://github.com/burgan-tech/vnext-ai-toolkit/pull/31 (released in toolkit v0.0.9)

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

## Resolution

Done in vnext-ai-toolkit v0.0.9 (burgan-tech/vnext-ai-toolkit#31):

- `component-task` emits type 23 only with `handler` / `input` / `timeoutSeconds`; `script` /
  `executionMode` are never generated, and the reviewer flags them as the removed pre-0.0.103 form.
- `templates/python-runtime/` scaffolds `handlers/` with `@handler`, a hash-pinned `requirements.txt` and
  `Dockerfile` `FROM ghcr.io/burgan-tech/vnext/python-runtime:<runtimeVersion>`; `/vnext-init` writes it
  only when a type-23 task exists, together with a compose override for the runtime + daprd sidecar.
- Verified: a type-23 task validates against vnext-schema 0.0.59, and the scaffolded image builds from
  `python-runtime:0.0.103` and answers `v1/invoke`. A full publish + execution against a runtime was not
  part of the toolkit's checks.

## Out of scope

- Building or pushing the domain image.
