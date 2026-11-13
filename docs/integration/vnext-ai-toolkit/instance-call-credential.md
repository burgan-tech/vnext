---
product: vnext-ai-toolkit
milestone: v0.0.106
status: pending
---

# Instance-call credential rule — Handoff for vnext-ai-toolkit

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

- Mapping templates that forward `sub` / `act_sub` from the request by hand into a SubFlow input mapping or an instance task's headers no longer need to: an absent credential is filled from the caller. Drop the manual forward.
- Teach the meaning of a blank value: to stop the caller's identity reaching the target, set the header to `""` (not omit it). Omitting it means "use the caller".
- Note that a SubFlow input mapping's `Headers` must be a `Dictionary<string, string?>` or an `ExpandoObject`, and that the rule covers SubFlow start and the `SubProcess`, `Start`, `DirectTrigger`, `GetInstance`, `GetInstances` and `GetInstanceData` tasks only.

## Acceptance

- Generated mappings and task scaffolds follow the rule and carry no redundant credential forwarding.

## Out of scope

- HTTP, SOAP, DaprService and DaprHttpEndpoint tasks: they keep the fill-when-blank rule.
