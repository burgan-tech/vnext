---
product: vnext-example
milestone: v0.0.105
status: pending
---

# Rule-based transition schemas (`schemas`) — Handoff for vnext-example

## Context

- vNext PR: the rule-based-schema PR of milestone v0.0.105
- Milestone: v0.0.105
- Issue: none

## What changed in the runtime

Every transition kind (state-level, shared, `startTransition`, `cancel`, `updateData`, `exit`) may
declare `schemas`, a rule-based list of schema entries, next to the unchanged single `schema`. The
shape is the one function `inputSchema` already uses: `[{ "rule"?: ScriptCode, "schema": Reference }]`
or the wrapped `{ "schemas": [...] }`. Entries are walked in declaration order; the first matching
`rule` wins; a rule-less entry is the fallback and must be last; `schemas` wins over `schema`;
automatic and scheduled transitions may not declare `schemas`. A declared list with no matching entry
rejects the transition with `400` `Transition:100025`; no schema declared accepts it.

Separately, a function whose rule-based `inputSchema` has no matching entry now rejects a request with
a body with `400` `Function:800006` (approved breaking change; add a rule-less last entry to keep
accepting unmatched callers).

Source of truth: `docs/runtime/function-contract-resolution.md` § Transitions and the vnext-meta
fragments `transition-rule-based-schemas.json` / `function-input-schema-no-match-rejects.json`.

## Contract and examples

```jsonc
"schemas": [
  { "rule": { "location": "./src/IsCorporate.csx", "code": "<base64>", "encoding": "B64" },
    "schema": { "key": "corporate-payload", "domain": "core", "flow": "sys-schemas", "version": "1.0.0" } },
  { "schema": { "key": "retail-payload", "domain": "core", "flow": "sys-schemas", "version": "1.0.0" } }
]
```

The rule is an `IConditionMapping` script; its context carries workflow, instance, runtime,
transition, `body` (instance latest data, `{}` when none) and request headers — no query parameters.

## What this product must do

- Add an integration scenario (README + `TEST-SCENARIOS.md` row) against the locally built runtime:
  1. rule match → transition validated against the matched schema;
  2. no match → `400` `Transition:100025`, instance keeps its state and is not faulted;
  3. no schema declared → accepted;
  4. function rule-based `inputSchema` no match → `400` `Function:800006`;
  5. schema function returns the matched entry, `404` on no match, and `304` on revalidation.
- Requires the vnext-schema release first (`npm run validate`).

## Acceptance

- The scenario is green; the `Schema.Validate` span and the unchanged instance row are confirmed via MCP.

## Out of scope

- Cross-domain variants.
