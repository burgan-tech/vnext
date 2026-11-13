---
product: vnext-ai-toolkit
milestone: v0.0.105
status: done
---

# Caller roles in selection rules (`context.Caller`) — Handoff for vnext-ai-toolkit

## Context

- vNext PR: the script-caller-roles PR of milestone v0.0.105 (stacked on the rule-based transition schemas PR)
- Milestone: v0.0.105
- Decision: council `2026-11-12-script-caller-roles` (Chair APPROVE_WITH_CONDITIONS)
- Sibling PR: https://github.com/burgan-tech/vnext-ai-toolkit/pull/33 (released in toolkit v0.0.10)

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

- Teach the component-authoring skills (views, schema/transition, function contracts, automatic transitions, timers, SubFlow mappings) that these scripts can use `await context.Caller.HasRoleAsync("x")` instead of parsing `Headers["role"]`.
- Scaffold timer scripts and automatic rules with an `IsAvailable` guard (System hops have no caller).
- State the limits in the generated guidance: selection rules only; positive tests with a least-privileged fallback; dynamic roles / combinators are not expanded.

## Acceptance

- A scaffolded role-based view or schema rule uses `context.Caller` and ends with a rule-less fallback.

## Resolution

Done in vnext-ai-toolkit v0.0.10 (burgan-tech/vnext-ai-toolkit#33):

- `references/concepts/csx-contracts.md` § Caller roles documents `HasRoleAsync`, `RolesAsync`,
  `IsAvailable`, `ResolutionFailed`, `RolesFromHeader`, the per-surface table, fail-closed `110006`, and
  the limits (selection is not authorization, positive tests, least-privileged fallback, `$` / dynamic
  grants and combinators not expanded).
- `workflow-scaffold`, `view-design`, `component-function`, `authoring-vnext-components` and
  `CLAUDE.md.tmpl` scaffold role rules with `await context.Caller.HasRoleAsync("x")` and a rule-less
  fallback; automatic rules and timers get the `IsAvailable` guard.
- The reviewer, the security checklist and domain-check check 21 flag `Headers.role` parsing,
  `context.Caller` in task / transition / event mappings, and unguarded reads on System hops.

## Out of scope

- Caller roles in task mappings, transition mappings, extensions, resource locks or event mappings (deliberately unavailable).
- Declarative `roles` on view/schema entries (a phase-2 candidate, not decided).
