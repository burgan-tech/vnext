---
product: vnext-example
milestone: v0.0.107
status: pending
---

# Instance-call credential rule — Handoff for vnext-example

## Context

- vNext PR: burgan-tech/vnext#1310 (branch `claude/subflow-subprocess-credentials-704267`)
- Milestone: v0.0.107
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

## Contract and examples

Summary of the rule (canonical statement: `docs/domain/role-grant-authorization.md#caller-credential-on-instance-calls`):

| What the mapping did | What the target sees |
|---|---|
| Set the key to a non-blank value | The mapping's value |
| Set the key to a blank value (`""`, whitespace, `null`) | The credential does NOT reach the target; the caller's value is NOT added |
| Did not set the key | The caller's value (the parent hop's request header) |

```csharp
// Excerpt: only InputHandler is shown.
public class StartChildWithoutCallerSub : ScriptBase, ISubFlowMapping
{
    // `sub` is blank on purpose: the child starts without it and the caller's value is not added.
    // `role` is not in the dictionary, so the caller's role is filled in.
    public Task<ScriptResponse> InputHandler(ScriptContext context) =>
        Task.FromResult(new ScriptResponse
        {
            Headers = new Dictionary<string, string?> { ["sub"] = "" }
        });

    // OutputHandler ... omitted
}
```

## Acceptance

- The scenarios are green against the locally built runtime, same-domain and, in the cross-domain lab, cross-domain.

## Out of scope

- HTTP, SOAP, DaprService and DaprHttpEndpoint tasks: they keep the fill-when-blank rule.
