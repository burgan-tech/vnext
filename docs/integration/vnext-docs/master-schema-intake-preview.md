---
product: vnext-docs
milestone: v0.0.107
status: pending
---

# Master-schema refusal at transition intake — Handoff for vnext-docs

## Context

- vNext PR: the master-schema intake preview PR of milestone v0.0.107
- Milestone: v0.0.107
- Issue: https://github.com/burgan-tech/vnext/issues/1255

## What changed in the runtime

A start or transition request is now validated against two schemas before it is admitted: the
transition's own schema, then — after the state-machine policy check — the flow's master schema,
applied to the instance data as it would look after the request body is merged in. A body the master
schema refuses is answered `400` `App:900002` immediately. Before, such a request was answered `200`
(sync) or `202` (async) and failed afterwards: the instance faulted, or the queued job failed after
the client had been told the request was accepted.

Source of truth: `docs/contracts/json-validation.md` § Two Validation Layers at Transition Intake.

## What this product must do

- Client-consumption pages on starting an instance and running a transition: state that a `400`
  `App:900002` can come from the master schema as well as from the transition schema, with the same
  response shape, and that an async request refused this way is never queued.
- Schema authoring page: note that a transition schema wider than the master schema is now refused at
  the request, not discovered as a faulted instance.
- Configuration reference: add `WorkflowExecution:InstanceDataWrite:IntakeMasterSchemaPreview`.

## Contract and examples

```jsonc
// 400 — same shape as a transition-schema refusal
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

| Key | Default | Effect |
|---|---|---|
| `WorkflowExecution:InstanceDataWrite:IntakeMasterSchemaPreview` | `true` | `false` restores the old behaviour: the master schema is checked only at the data append |

Not previewed, so still validated only at the append: transitions with a `mapping`, keys forwarded to
an active SubFlow, automatic / scheduled / event hops, schemas with `x-encryption` paths, bodies
carrying a file node.

## Acceptance

- The pages above describe the two layers and the single refusal shape; the config key is listed.

## Out of scope

- Custom validation rules: they stay off in the preview, as they are at the append.
