---
product: vnext-example
milestone: v0.0.107
status: pending
---

# List queries are refused on fields the caller cannot read — Handoff for vnext-example

## Context

- vNext PR: to be linked when the pull request is opened (branch `sprint24-26/StopReturnProtectedValues`)
- Milestone: v0.0.107
- Issue: <https://github.com/burgan-tech/vnext/issues/1262>

## What changed in the runtime

The instance list and the `GetInstances` task refuse a `filter`, `sort`, `groupBy` or `aggregations`
part that names a master-schema field the caller may not read as stored (`x-roles` not granted for
every row, or `x-masking` without an exemption). Every master-schema refusal of the list is now
returned as a validation error, so a same-domain `GetInstances` task fails with status 400 where it
used to fail with 500. Source of truth:
[field-masking § Queries](../../domain/field-masking.md#queries).

## What this product must do

The runtime change is unit-tested only; the end-to-end proof is this scenario, and it gates the merge
of the vNext PR (council decision `2026-11-26-list-query-guarded-fields-1262-pr-gate`, condition M-1).

- One scenario in `tests/Core.IntegrationTests`, with its README and a `TEST-SCENARIOS.md` row, against
  the locally built runtime. Extend an existing field-protection or row-security scenario if one fits.
- A flow whose master schema has one `x-roles` field, one `x-masking` field with an exemption role, one
  `x-masking` field without `roles`, and one open field.

## Contract and examples

| Case | Expected |
|---|---|
| Role without the grant: filter / sort / groupBy / `max` on the `x-roles` field, each in its own request | 400 `Validation:900010`; no guarded value anywhere in the body |
| Role with the grant: the same four requests | 200 |
| Exempt role filtering the masked field; any role on the mask without `roles` | 200; 400 |
| Query on the open field, any role | 200 |
| `GetInstances` task with a role in its headers, filter on the guarded field | task succeeds |
| `GetInstances` task with no credential (automatic hop), same filter | task fails, status 400, on every run |
| Same task with `acceptedStatusCodes` admitting `4xx` | task "succeeds" with no rows — assert it, so the hazard stays visible |
| Same task with and without an error boundary | boundary rule on `400` matches; without one the transition faults |

Capture the HTTP status and body of one 400, and of a refusal that existed before the change (a filter
on an `encrypt` field), for the before / after comparison the PR needs.

## Acceptance

The scenario is green against the locally built runtime at a commit of the vNext PR, and its TRX is
the input of that PR's `## E2E evidence` block.

## Out of scope

The role-provider-outage answer (503 `Authorization:110006`) needs a failing provider and is not part
of this scenario.
