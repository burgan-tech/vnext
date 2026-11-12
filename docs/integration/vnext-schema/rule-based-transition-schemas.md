---
product: vnext-schema
milestone: v0.0.105
status: pending
---

# Rule-based transition schemas (`schemas`) — Handoff for vnext-schema

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

- Add an optional `schemas` property to the transition definition (all transition kinds), accepting
  the array form and the wrapped `{ "schemas": [...] }` form; each entry has an optional `rule`
  (ScriptCode) and a required `schema` (Reference), no extra properties.
- Keep `schema` unchanged; both may appear (runtime prefers `schemas`).
- Release before domain packages use the field — until then `npm run validate` rejects it.

## Acceptance

- A workflow with `schemas` on a state transition, a shared transition and `updateData` validates.
- An entry without `schema` fails validation.

## Out of scope

- Enforcing "rule-less entry last" and "no schemas on automatic/scheduled" — the runtime validator does it.
