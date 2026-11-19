---
product: vnext-docs
milestone: v0.0.107
status: pending
---

# A transition's view link carries its binding's loadData — Handoff for vnext-docs

## Context

- vNext PR: burgan-tech/vnext — `fix(state-function): honour the transition view binding's loadData`
- Milestone: v0.0.107
- Issue: https://github.com/burgan-tech/vnext/issues/1266 (source: https://github.com/burgan-tech/vnext-client-sdk-core/issues/120)

## What changed in the runtime

- Each `transitions[]` entry of the state function answers `view.loadData` with the `loadData`
  declared on that transition's view binding. Before, a transition outside an active subflow always
  answered `true` — a form declared `loadData: false` opened pre-filled with the record's data — and
  a parent-added shared transition listed while a subflow runs always answered `false`.
- A subflow's own entries carry what the child declares (the parent passes the child's answer on).
- With several view entries the state function does not evaluate their rules: the **first** entry's
  `loadData` is used, as for the state's own view link.
- Unchanged: a binding that declares no `loadData` answers `true` for the instance's own transitions
  and `false` for a parent-added shared transition; a transition without a view answers
  `hasView: false`, `loadData: true`; scheduled entries stay `hasView: false`, `loadData: false`.
- `StateFunctionCache.ResponseShapeVersion` moved to `v15`: a parked long-poller gets the corrected
  body once (200 instead of 304).

Source of truth: `vnext-meta` migration `transition-view-load-data-honoured`;
`docs/runtime/state-function-cache-and-etag.md`.

## What this product must do

- In the client-consumption / state function page, describe `transitions[].view.loadData` as the
  value declared on the transition's view binding, with the table below.
- In the workflow authoring page for transition views, state that `loadData: false` makes the form
  open empty and that an undeclared `loadData` is treated as `true` for a transition form.

## Contract and examples

```json
{ "key": "request-info", "target": "...", "view": { "view": { "key": "info-form", "domain": "core", "version": "1.0.0", "flow": "sys-views" }, "loadData": false } }
```

State function entry:

```json
{ "name": "request-info", "view": { "href": ".../functions/view?transitionKey=request-info", "hasView": true, "loadData": false } }
```

| Binding | `hasView` | `loadData` |
|---|---|---|
| `loadData: false` | true | false |
| `loadData: true` | true | true |
| no `loadData` | true | true |
| no view | false | true |
| several entries | true | the first entry's |

## Acceptance

- The pages show the table and say that the first entry decides when several views are declared.

## Out of scope

- The view function's own response (it does not carry `loadData`).
- Changing the default for an undeclared binding — a separate product decision.
