---
product: vnext-docs
milestone: v0.0.107
status: pending
---

# Transition history filtering and header deny list — Handoff for vnext-docs

## Context

- vNext PR: (link added when the PR opens) — branch `claude/issue-112-analysis-55b8ce`
- Milestone: v0.0.107
- Issue: https://github.com/burgan-tech/vnext/issues/1260 (source: vnext-client-sdk-core#112)
- Example scenarios: vnext-example `Tests/RoleMatrixLab/TransitionHistoryExposureTests`,
  `Tests/HeaderExposureLab`

## What changed in the runtime

- `GET …/instances/{id}/transitions` passes each row's recorded request **body** through the master
  schema's field rules (`x-roles`, `x-masking`, `x-encryption`) for the caller, like instance GET and
  the data function. If the plan cannot be built, the body is `null` (fail-closed).
- `Workflow:HeaderExposure:Deny` lists header names a domain keeps out of the transition record, the
  history response and every script/mapping `context.Headers`. Deny only; empty by default, and
  empty changes nothing. The runtime's role, identity and credential reads are unaffected.
- `createdBy` / `createdByBehalfOf` on history rows are returned as recorded (runtime audit columns).

Source of truth: `docs/runtime/header-exposure.md`, `docs/domain/field-masking.md`.

## What this product must do

- Transition history page (client consumption): state that the body is filtered per caller by the
  master schema and that only fields the schema declares are protected.
- Configuration page: add `Workflow:HeaderExposure:Deny` with the contract above, the example below
  and the consequence for mappings (a mapping no longer sees a listed header).

## Contract and examples

```json
"Workflow": {
  "HeaderExposure": { "Deny": [ "authorization", "cookie", "x-api-key" ] }
}
```

## Acceptance

- A domain developer can configure the list and predict which surfaces lose the header from the docs
  alone.

## Out of scope

- A recommended default list: the runtime ships none (Chair decision 2026-11-23).
