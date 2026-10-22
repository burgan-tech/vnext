---
product: vnext-docs
milestone: v0.0.102
status: pending
---

# Flow default version (#1106) — Handoff for vnext-docs

## Context

- vNext PR: (link added when the PR opens)
- Milestone: v0.0.102
- Issue: https://github.com/burgan-tech/vnext/issues/1106

## What changed in the runtime

Publishing a flow version no longer activates it automatically when the flow has a default version.
A domain team pins a flow's default in the `sys-version-registry` system flow (shipped by the
`@burgan-tech/vnext-core` package); versionless starts, `latest` and partial references (`2`, `2.1`) resolve
to that default, and newer published versions stay pending until an explicit version is requested or the
default moves. Source of truth: `docs/runtime/flow-default-version.md` in the vnext repo and the
`engine.flowDefaultVersion` vnext-meta entry.

## What this product must do

- Add a "Flow default version" page to the technical docs: concepts (published / latest / default /
  effective default / pending / explicit), the three pin modes (exact, minor line, major line) and
  `latest` (automatic, also the way to clear a default).
- Document the registry contract and the two calls (create, `set-default`) with examples.
- Add the rollout runbooks: pilot a patch, pilot a minor while hotfixes flow, cross-domain
  onboarding → contract.
- Business docs: one paragraph on "Publish ≠ Activate" for product owners.

## Contract and examples

Registry instance: key = flow key; data `{ "type": "Flow", "defaultVersion": "2.1.0", "reason": "CHG-12345" }`.

```http
POST /api/v1/{domain}/workflows/sys-version-registry/instances/start?sync=true
{ "key": "customer-onboarding", "attributes": { "type": "Flow", "defaultVersion": "2.1.0" } }

PATCH /api/v1/{domain}/workflows/sys-version-registry/instances/{id}/transitions/set-default?sync=true
{ "attributes": { "type": "Flow", "defaultVersion": "latest" } }
```

Accepted `defaultVersion`: `2`, `2.1`, `2.1.0`, `2.1.0-pkg.1.0.168`, `latest`; `null` only at create.
Errors: `Instance:100051` (404, only pending versions match), `Instance:100052` (409, default matches no
published version — also rejected when written), `Instance:100053` (503, registry unreadable).

## Acceptance

The docs page reproduces the scenario table of `docs/runtime/flow-default-version.md` and the three
runbooks; examples match the contract above.

## Out of scope

A read surface for "which version is live" (not built); Function default versions.
