---
product: vnext-docs
milestone: v0.0.104
status: pending
---

# Sync response carries the instance state — Handoff for vnext-docs

## Context

- vNext PR: opened from branch `claude/issue-1182-analysis-dc22e7`
- Milestone: v0.0.104
- Issue: https://github.com/burgan-tech/vnext/issues/1182

## What changed in the runtime

- A **sync** (`?sync=true` or `executionType: S`) start / transition response now carries
  `currentState` and `effectiveState` next to `status` / `effectiveStatus`. Same names and values as
  the instance GET's `metadata.currentState` / `metadata.effectiveState` (persisted columns, no
  SubFlow descent — not the state function's `state`).
- The runtime-internal identity-only answer (cross-domain `sub/instances/start`, subflow forward) also
  carries both, so a **sync SubProcess task** output now holds the child's state.
- An async `202` carries neither. Purely additive; no DB read was added.
- Source of truth: `docs/runtime/execution-type.md` § A sync response carries the state, free of
  charge; `vnext-meta/unreleased/sync-response-state.json`.

## What this product must do

- Add the two fields to the sync start / transition response reference.
- In the SubProcess task page, show how an output mapping reads the child's state, and that the path
  differs by route (same domain vs cross-domain).

## Contract and examples

```json
// POST .../instances/start?sync=true — 200
{ "id": "8d83…", "status": "A", "effectiveStatus": "A",
  "currentState": "otp-waiting", "effectiveState": "otp-waiting" }
```

```csharp
// SubProcess (sync) output mapping
var state = context.Body?.data?.value?.currentState   // same-domain start
         ?? context.Body?.data?.currentState;         // cross-domain start
```

## Acceptance

- Both fields documented on the sync response, marked absent on `202`.
- The SubProcess page has the mapping snippet with both paths.

## Out of scope

- The state function body (unchanged) and async polling guidance.
