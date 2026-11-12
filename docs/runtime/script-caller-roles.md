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

Only rule-based **selection** on a caller's request:

- transition `schemas` (write validation and the `functions/schema` read),
- function `inputSchema` / `outputSchema` / `inputView` / `outputView` rules (execute and `/info`, `/view`, `/schema`),
- state / transition `views` rules (`functions/view`).

Everywhere else — task mappings, OnEntry/OnExit/OnExecute, automatic-transition rules, timers, event
handlers, subflow callbacks, the Execution host — `IsAvailable` is false and a read throws. There is no
caller to ask for in the background, and an empty set would be indistinguishable from "holds no role".

On the function execute path the accessor is **closed** after input-schema selection, before the cache key
expression and the tasks run: the function response cache does not vary by role, so a task must not read it.

## Cost

Nothing is resolved until a rule reads roles. A rule that never reads them costs no provider call. Within
one request the provider answers at most once — the accessor memoizes, and `morph-idm` already memoizes per
request scope and credential, shared with every other surface of the request (authorization, cache keys).
Across requests there is no cache: every request whose rule reads roles makes one provider call.

## When the provider cannot answer

If a rule reads roles and the provider fails (morph-idm error, timeout, open circuit breaker), the selection
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
