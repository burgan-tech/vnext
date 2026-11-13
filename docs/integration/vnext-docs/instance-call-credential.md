---
product: vnext-docs
milestone: v0.0.106
status: pending
---

# Instance-call credential rule — Handoff for vnext-docs

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

- Update the authorization and SubFlow pages to describe the rule and link the runtime's canonical section; state the rule once, do not copy the table into several pages.
- Document the behaviour changes: a same-domain SubFlow now carries the caller's credential by default (a child state with `interaction.longPoll.roles` pauses for the caller's roles unless the parent blanks `role`); a blank credential in a trigger task's headers is no longer filled from the caller; `sub` / `act_sub` are no longer completed from Activity baggage.
- Document the known difference: a cross-domain SubFlow start also forwards `userId`, `given_name`, `family_name`, `act_uid`, `consent_id` from the ambient user; a same-domain start does not.
- Document that a SubFlow input mapping's `Headers` must be a `Dictionary<string, string?>` or an `ExpandoObject`, and that HTTP, SOAP, DaprService and DaprHttpEndpoint tasks keep the older fill-when-blank rule.

## Acceptance

- The docs portal states the three-row rule and the same-domain child identity (`CreatedBy` = `act_sub`, `CreatedByBehalfOf` = `sub`) and agrees with the runtime page.

## Out of scope

- A configuration switch for the old behaviour: there is none.
