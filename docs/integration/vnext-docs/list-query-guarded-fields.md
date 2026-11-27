---
product: vnext-docs
milestone: v0.0.107
status: pending
---

# List queries are refused on fields the caller cannot read — Handoff for vnext-docs

## Context

- vNext PR: to be linked when the pull request is opened (branch `sprint24-26/StopReturnProtectedValues`)
- Milestone: v0.0.107
- Issue: <https://github.com/burgan-tech/vnext/issues/1262>

## What changed in the runtime

The instance list (`GET …/instances` with `filter`, `sort`, `groupBy`, `aggregations`) and the
`GetInstances` task used to run those query parts on the stored value of every field. A grouped
response never passes through the per-instance exposure pass, so a group key or a `min` / `max`
returned a value hidden by `x-roles` or `x-masking`, and a filter or a sort order could be used to
probe it. The list now refuses a query part that names a field the caller may not read as stored.

Source of truth: [field-masking § Queries](../../domain/field-masking.md#queries); migration
`list-query-refused-on-guarded-fields` and the two known issues in
`vnext-meta/unreleased/list-query-guarded-fields.json`.

## What this product must do

- Instance filtering / grouping page: add the refusal, the two answers and the "decided for the whole
  result set" rule (an instance-dependent grant never opens a field for a query).
- Field protection (`x-roles`, `x-masking`) page: a field hidden from a role cannot be filtered,
  sorted, grouped or aggregated by that role; a mask without `roles` cannot be queried by anybody.
- `GetInstances` task page: the task is judged by the credential in its headers; with no role it is
  refused on every run; `acceptedStatusCodes` that admit `4xx` hide the refusal as an empty result.
- Known issues page: the two limits below.

## Contract and examples

| Situation | Answer |
|---|---|
| Query part names a field the caller cannot read (also its parent object or a sub-path) | 400 `Validation:900010`, message names the field the caller sent |
| Same query while the role provider could not answer | 503 `Authorization:110006`, retryable (a flow with `rowSecurity` answers an empty page instead) |
| Same-domain `GetInstances` task hitting any master-schema refusal | task failure with status 400 (was 500) |

Limits to document as known issues: the guard reads the **default** flow version's master schema only
(`list-query-guards-follow-default-version`); with no readable master schema nothing is guarded
(`list-query-unguarded-without-schema-context`).

## Acceptance

The four pages state the behaviour above; no page claims that a guarded field is protected in every
flow version, or that the list fails closed when the schema cannot be read.

## Out of scope

Row security (`attributes.rowSecurity`) is unchanged: its rules may name a guarded field and are not
refused.
