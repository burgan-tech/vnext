---
product: vnext-example
milestone: v0.0.107
status: pending
---

# Master-schema refusal at transition intake — Handoff for vnext-example

## Context

- vNext PR: the master-schema intake preview PR of milestone v0.0.107
- Milestone: v0.0.107
- Issue: https://github.com/burgan-tech/vnext/issues/1255

## What changed in the runtime

A request body that the transition's own schema accepts but the flow's master schema refuses used to
be admitted (`200` / `202`) and failed later at the data append, faulting the instance or failing the
queued job. Transition validation now ends with a side-effect-free preview of that append, so the
request is answered `400` `App:900002` before admission, sync and async, at start and on a transition.
The order is transition schema → state-machine policy → master-schema preview.

The write funnel is still the authority. The preview accepts and leaves the verdict to it for mapped
transitions, forwarded SubFlow keys, system-triggered hops, schemas with `x-encryption` paths, file
nodes and any unexpected failure of the preview itself.

Source of truth: `docs/contracts/json-validation.md` § Two Validation Layers at Transition Intake and
the vnext-meta fragment `intake-master-schema-preview.json`.

## What this product must do

- Add the integration scenario `MasterSchemaIntakeLab` (flow `master-schema-intake-lab`, closed master
  schema `msi-lab-master`), with its README and a `TEST-SCENARIOS.md` row. It is written on the
  vnext-example branch `test/master-schema-intake-1255`; open that branch as a PR.

## Contract and examples

```jsonc
// 400 — a body member the closed master schema does not declare
{
  "error": {
    "prefix": "validation",
    "code": "App:900002",
    "message": "JSON schema validation failed",
    "validationErrors": [
      { "message": "All values fail against the false schema", "members": ["actor"] }
    ]
  }
}
```

Kill switch: `WorkflowExecution:InstanceDataWrite:IntakeMasterSchemaPreview` (default `true`).

## Acceptance

- The scenario is green against the locally built runtime (7 tests / 10 cases).
- A refused request leaves no trace: the instance is neither `Busy` nor `Faulted`, no data row carries
  the refused member, no job row exists for a refused async request, and the refused traces contain no
  write span — confirmed against Postgres and the `Schema.ValidateMaster` span.

## Out of scope

- Reproducing the pre-fix fault on an older runtime.
- Mapped transitions: their body is not what gets appended, so they are not previewed.
