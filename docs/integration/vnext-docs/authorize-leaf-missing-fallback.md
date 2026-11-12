---
product: vnext-docs
milestone: v0.0.104
status: pending
---

# authorize?queryRoles while a SubFlow child is being created — Handoff for vnext-docs

## Context

- vNext PR: the authorize leaf-missing fallback PR of milestone v0.0.104
- Milestone: v0.0.104
- Issue: https://github.com/burgan-tech/vnext/issues/1181

## What changed in the runtime

`GET …/instances/{id}/functions/authorize?queryRoles=true` is decided at the deepest active SubFlow
leaf. A parent commits its SubFlow correlation before the child's row exists (the child is created by
a post-commit job, after the subflow input mapping). Up to v0.0.103 a call landing in that window was
refused: the child could not be loaded, answered `allowed: false` without evaluating a grant, and the
parent passed that up — the internal gateway then answered `403` to the client's state poll.

From v0.0.104:

- While the child does not exist yet, the **deepest existing instance** decides with its own
  `queryRoles` resolution (stamped parent override → state `queryRoles` → workflow `queryRoles`) — the
  same view the `state` function serves in that window.
- The authorize response body gains an optional field: `reason: "instance-not-found"` when the
  addressed instance could not be loaded (no grant was evaluated; still `403`). An ordinary decision
  carries no `reason` — the body is `{"allowed": true|false}` as before.
- Only `queryRoles` falls back; a `transitionKey` / `functionKey` forwarded to a missing child stays
  refused.

Source of truth when this page and the runtime disagree: `AuthorizeAppService`
(`src/BBT.Workflow.Application/Authorization/`) and
[docs/domain/authorize-function.md](../../domain/authorize-function.md) § `?queryRoles=true`.

## What this product must do

- `docs/concepts/authorization` ("Yaprak-tek karar"): add that the leaf is the deepest instance that
  exists — while a SubFlow child is still being created, its parent decides.
- The authorize function's response description: document the optional `reason` field and its only
  value `instance-not-found`, and that it marks a refusal that evaluated no grant.
- Gateway / client guidance: a `403` with `reason: instance-not-found` on a directly addressed instance
  means the instance does not exist (yet), not that the caller lacks a role.
