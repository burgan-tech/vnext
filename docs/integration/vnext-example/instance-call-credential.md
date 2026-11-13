---
product: vnext-example
milestone: v0.0.106
status: pending
---

# Instance-call credential rule — Handoff for vnext-example

## Context

- vNext PR: the instance-call credential rule PR (branch `claude/subflow-subprocess-credentials-704267`)
- Milestone: v0.0.106
- Decision: product-owner decisions of 2026-11-13 (rule kept explicit in docs and code comments; no switch for the old behaviour)

## What changed in the runtime

When the runtime calls another instance (SubFlow start, `SubProcess` / `Start` / `DirectTrigger` tasks, `GetInstance` /
`GetInstances` / `GetInstanceData` tasks), the credential headers `sub`, `act_sub`, `position`, `client_id`, `role` follow
one rule: the mapping decides, blank included; only an ABSENT credential is filled from the caller's request headers.
Same-domain and cross-domain calls give the same outcome. Source of truth: `docs/domain/role-grant-authorization.md#caller-credential-on-instance-calls` (the canonical section), vnext-meta
fragment `instance-call-credential-rule.json`.

## What this product must do

- Add integration scenarios (Task 8 of the runtime plan) covering each row of the rule for a SubFlow start and for the `SubProcess`, `Start`, `DirectTrigger` and `Get*` tasks, same-domain and cross-domain: a non-blank mapping value wins; a blank value (`""`, whitespace, `null`) keeps the credential from the target; an absent key is filled from the caller.
- Assert the same-domain child row: `CreatedBy` = `act_sub`, `CreatedByBehalfOf` = `sub`.
- Assert that a child whose parent blanked `sub` does not see the first caller's `sub` (no baggage fallback), and that a hop without a caller (timer, automatic transition) adds no credential.
- Each scenario gets a README and a row in `TEST-SCENARIOS.md` in the same commit; extend an existing scenario before adding one.

## Acceptance

- The scenarios are green against the locally built runtime, same-domain and, in the cross-domain lab, cross-domain.

## Out of scope

- HTTP, SOAP, DaprService and DaprHttpEndpoint tasks: they keep the fill-when-blank rule.
