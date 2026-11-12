---
product: vnext-docs
milestone: v0.0.105
status: pending
---

# Caller roles in selection rules (`context.Caller`) — Handoff for vnext-docs

## Context

- vNext PR: the script-caller-roles PR of milestone v0.0.105 (stacked on the rule-based transition schemas PR)
- Milestone: v0.0.105
- Decision: council `2026-11-12-script-caller-roles` (Chair APPROVE_WITH_CONDITIONS)

## What changed in the runtime

Scripts can read the caller's role set as the runtime resolved it — selection rules (transition `schemas`,
function `inputSchema`/`outputSchema`/`inputView`/`outputView`, state/transition `views`), automatic-transition
rules and timer scripts on caller-driven hops, and SubFlow output mappings (the child settler's identity):

```csharp
public class IsApprover : ScriptBase, IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context) =>
        await context.Caller.HasRoleAsync("morph-idm.approver");
}
```

`ScriptContext.Caller` (`ICallerAccessor`): `HasRoleAsync`, `RolesAsync`, `IsAvailable`, `ResolutionFailed`,
`RolesFromHeader`. Resolved lazily (one provider call per request scope at most).

| Surface | Whose roles | Provider failure |
| --- | --- | --- |
| Selection rules (schemas, function contract slots, views) | the request's caller | selection fails: `503` `Authorization:110006` |
| Automatic-transition rule | the caller who drove the hop (request, async job, inline auto chain); none on System hops | step fails (`110006`); the DefaultAutoTransition is not taken |
| Timer script | the caller who entered the state (computed at arming); none when a fired timer re-arms | step fails (`110006`) |
| SubFlow output mapping | the child settler (carried headers), not the parent's starter; none when a timer settled the child | completion redelivered; the parent is not faulted |
| Task mappings, transition mapping, extensions, resource lock, event mappings | — (unavailable; a read throws) | — |

Where there is no caller, `IsAvailable` is false — never an empty set; scripts that may run there must guard it.
Selection by role is not authorization. Decisions: councils `2026-11-12-script-caller-roles` and
`2026-11-12-script-caller-roles-background` (Chair override). Source of truth: `docs/runtime/script-caller-roles.md`, vnext-meta fragment `script-caller-roles.json`.

## What this product must do

- Document `context.Caller` on the scripting page: API table, the per-surface availability table above, cost, and the `IsAvailable` guard for timers / automatic rules.
- Add `Authorization:110006` (503, retryable) to the error-code list; explain the fail-closed behaviour during a role-provider outage.
- State that role-based selection is UX/contract selection, not authorization, and the gateway must strip a client `role` header.

## Acceptance

- The pages cover the API, availability, outage behaviour and the selection-not-authorization note.

## Out of scope

- Caller roles in task mappings, transition mappings, extensions, resource locks or event mappings (deliberately unavailable).
- Declarative `roles` on view/schema entries (a phase-2 candidate, not decided).
