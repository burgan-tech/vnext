---
product: vnext-ai-toolkit
milestone: v0.0.107
status: pending
---

# Instance-call credential rule — Handoff for vnext-ai-toolkit

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

- Mapping templates that forward `sub` / `act_sub` from the request by hand into a SubFlow input mapping or an instance task's headers no longer need to: an absent credential is filled from the caller. Drop the manual forward.
- Teach the meaning of a blank value: to stop the caller's identity reaching the target, set the header to `""` (not omit it). Omitting it means "use the caller".
- Note that a SubFlow input mapping's `Headers` may be a string dictionary, an object dictionary / `ExpandoObject`, or a JSON object — any other shape, an anonymous object or a script-defined class included, fails the SubFlow start — and that the rule covers SubFlow start and the `SubProcess`, `Start`, `DirectTrigger`, `GetInstance`, `GetInstances` and `GetInstanceData` tasks only.

## Contract and examples

What a scaffolded mapping should and should not write (canonical statement: `docs/domain/role-grant-authorization.md#caller-credential-on-instance-calls`):

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

Write `["sub"] = ""` to block the identity; do not write `["sub"] = <request sub>` by hand, and do not omit the key when the identity must be blocked.

## Acceptance

- Generated mappings and task scaffolds follow the rule and carry no redundant credential forwarding.

## Out of scope

- HTTP, SOAP, DaprService and DaprHttpEndpoint tasks: they keep the fill-when-blank rule.
