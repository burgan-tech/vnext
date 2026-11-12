# Caller Roles in Rule Scripts (`context.Caller`)

A rule that selects a schema or a view can ask for the caller's roles as the runtime resolved them:

```csharp
public class IsApprover : ScriptBase, IConditionMapping
{
    public async Task<bool> Handler(ScriptContext context) =>
        await context.Caller.HasRoleAsync("morph-idm.approver");
}
```

The set comes from the configured caller-role provider — the same set the runtime uses for its own role
decisions and for caller-scoped cache keys:

| Provider | Source |
| --- | --- |
| `default` | token claims (`ICurrentUser.Roles`), else the gateway-supplied `role` header |
| `morph-idm` | the identity service (`get-roles`); a non-blank `role` header **replaces** that answer |

Prefer `context.Caller` over parsing `Headers["role"]`: under `morph-idm` the header is not the caller's
role set unless the gateway put it there.

Decision record: council session `2026-11-12-script-caller-roles` (log row in
[`docs/agent-council/sessions/README.md`](../agent-council/sessions/README.md)).

## API

`ScriptContext.Caller` is an `ICallerAccessor` (`BBT.Workflow.Scripting.Caller`), never null:

| Member | Meaning |
| --- | --- |
| `HasRoleAsync(role)` | true when the caller holds `role` (ordinal comparison) |
| `RolesAsync()` | the caller's roles; empty when they hold none |
| `IsAvailable` | false on a surface with no caller (see below) |
| `ResolutionFailed` | true once a read failed because the provider could not answer |
| `RolesFromHeader` | after a read: whether the set came from the request's `role` header (diagnostic only) |

Reads throw `CallerRolesUnavailableException` when the surface has no caller or the provider could not answer.

## Where it is available

Every script sees `context.Caller`; whether it can read roles depends on the surface. Each decision is pinned in
a code comment at the wiring site, and was taken by two council sessions: `2026-11-12-script-caller-roles`
(selection surfaces) and `2026-11-12-script-caller-roles-background` (automatic rules, timers, SubFlow output
mapping — added by a Chair override of the council's narrower proposal).

| Surface | Available | Whose roles | When the provider fails |
| --- | --- | --- | --- |
| Transition `schemas` rule (write validation, `functions/schema`) | Yes | the request's caller | selection fails: `503` `Authorization:110006` |
| Function `inputSchema` / `outputSchema` / `inputView` / `outputView` rule | Yes | the request's caller | selection fails: `503` `Authorization:110006` |
| State / transition `views` rule (`functions/view`) | Yes | the request's caller | selection fails: `503` `Authorization:110006` |
| Automatic-transition `rule` | Yes, on caller-driven hops | the caller who drove the hop: the request, the async-accept job (it replays the caller's headers), and auto hops chained inline after them | the step fails (`Authorization:110006`, retryable) through the error boundary; the DefaultAutoTransition is **not** taken |
| Timer script (`ITimerMapping`) | Yes, when the state is entered by a caller | the caller who drove the hop that **entered** the state — timers are computed at arming, not at firing | the step fails (`Authorization:110006`) |
| SubFlow output mapping (completion) | Yes, when the child's settling request carried headers | the **child settler** — the caller whose request completed the child, from the headers carried on `instance.sub.completed` and re-filtered by the carried-header policy; not the parent's starter | transient: the completion is rolled back and redelivered (relay / Inbox), the parent is **not** faulted; roles are resolved before the terminal lock |
| SubFlow output mapping on the fault path | as above | as above | logged as a failed mapping (existing fault-path behaviour); settlement continues |
| Task mappings (OnEntry, OnExit, OnExecute, function tasks), transition `mapping`, extensions, resource lock, event mappings | No | — | — |

"No caller" means `IsAvailable` is false and a read throws — never an empty set:

- **System hops** — a fired timer (`TransitionTimerJobHandler`, `ExecutionActor.System`, no headers), a SubFlow
  resume, a continuation — have no caller. An automatic rule there, or a timer script whose state was entered by
  such a hop (a timer that **re-arms** after firing), cannot read roles. Guard with `IsAvailable` and pick a
  default:

  ```csharp
  public async Task<TimerSchedule> Handler(ScriptContext context)
  {
      var vip = context.Caller.IsAvailable && await context.Caller.HasRoleAsync("vip");
      return TimerSchedule.FromDuration(vip ? TimeSpan.FromHours(48) : TimeSpan.FromHours(24));
  }
  ```

- A **child settled by a timer** carries no headers, so its parent's output mapping has no caller.

The automatic-rule context is shared with the hop's task mappings; the accessor is exposed for the rule being
evaluated only and closed afterwards, so a task mapping never sees it. On the function execute path the accessor
is closed after input-schema selection, before the cache key expression and the tasks (the response cache does
not vary by role).

**Known, separate:** an automatic rule that throws for any other reason still reads as "not satisfied" (the next
automatic transition or the DefaultAutoTransition may fire). Closing that for every rule is a behaviour change
and is tracked separately; only the caller-roles failure is closed here.

## Cost

Nothing is resolved until a script reads roles. A rule that never reads them costs no provider call. Within
one request the provider answers at most once — the accessor memoizes, and `morph-idm` already memoizes per
request scope and credential, shared with every other surface of the request (authorization, cache keys).
A chain of automatic hops run inline shares one scope and one call; every new scope (an async job, a SubFlow
completion) makes its own. Across requests there is no cache. An automatic rule or timer script waits for the
provider while the instance is Busy (no transaction or lock lease is held); the morph-idm circuit breaker was
observed not to open on timeouts, so an outage costs up to the provider timeout (5 s) per scope.

## When the provider cannot answer

If a script reads roles and the provider fails (morph-idm error, timeout, open circuit breaker), the outcome is
decided by the runtime, per surface (table above) — never a fallback. On request surfaces the selection
**fails** with `503` `Authorization:110006` (`CallerRolesUnavailable`, retryable) instead of falling through
to a later entry or the fallback. This holds whatever the rule did with the exception — returned false,
rethrew, or swallowed it and returned true: the selection loops check `ResolutionFailed` after every rule.
Rules that do not read roles keep working during the outage. A failed selection is never cached.

Without this, a provider outage would turn every role rule into "no match" and serve the fallback entry —
usually the loosest contract — to callers who were never identified. An ordinary rule error (a script bug)
is still skipped as before.

## Selection is not authorization

Choosing a form or a payload contract by role is a UX / contract decision. It does not protect anything:
the transition endpoint does not enforce `transition.roles`, and access decisions stay with the gateway and
`queryRoles` ([role-grant authorization](../domain/role-grant-authorization.md)). Write rules as positive
tests (`HasRoleAsync("x")`), and make the rule-less fallback the least privileged contract.

Not expanded by the accessor: predefined roles (`$InstanceStarter`, `$PreviousUser`, …), dynamic grants
and `allOf` / `anyOf` combinators — it answers plain role names only.

## Deployment precondition

Under both providers a client-supplied `role` header is taken at face value (`default`: when the token has
no role claim; `morph-idm`: it replaces the identity service's answer). The gateway must strip or overwrite
it. `RolesFromHeader` tells a rule author, while debugging, which source answered.
