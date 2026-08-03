# Role Grant Authorization

Every authorization surface in vNext evaluates the same thing: a **grant set** (`RoleGrant[]`) against
the **caller's roles**. The grant sets differ by surface, the rule does not.

| Surface | Grant set | Enforced at |
|---|---|---|
| Custom function call | `function.roles` | `FunctionAppService.ExecuteFunctionAsync` → 403 `FunctionAccessDenied` |
| Built-in `state` / `view` / `data` / `schema` | state `queryRoles`, else workflow `queryRoles` | `InstanceQueryAppService.IsInstanceQueryAllowedAsync` → 403 `QueryAccessDenied` |
| `availableTransitions` discovery | `transition.roles` | `FilterAuthorizedTransitionKeysAsync` (filtered out, not rejected) |
| `functions/authorize` | any of the above, by target | `AuthorizeAppService` → 403 |
| Human-task list | `transition.roles` (+ parent overrides) | `FilterAuthorizedInstancesAsync` (filtered out) |
| Schema field visibility | schema `x-roles` per property path | `SchemaFieldFilterService` (field pruned from the body) |
| Long-poll acknowledge | `state.interaction.longPoll.roles` | `InstanceCommandAppService` → 403 `LongPollAckAccessDenied` |

## Transition execution is deliberately not role-gated

`transition.roles` is a **discovery** control. `POST .../instances/{instance}/transitions/{key}` does
**not** evaluate it — this is an intentional design decision, not an oversight.

`InstanceCommandAppService.TransitionAsync` validates through `TransitionValidationService`: transition
schema validation, then `TransitionExecutionPolicy`. None of that policy's specifications reads
`transition.Roles`; `ActorAuthorizationSpecification` gates trigger-type against actor (User/System),
which is a different concern. In production `IsTransitionAllowedForRoleAsync` has exactly one caller —
`AuthorizeAppService`, serving the `authorize` function.

Consequences to design around:

- A caller who knows a transition key can execute it even when `roles` would exclude it from their
  `availableTransitions`. Treat `roles` as *what the client should offer*, not as a capability boundary.
- Anything that must be an actual boundary belongs in `queryRoles` (which **is** enforced, with 403),
  in a function's `roles` (also 403), or in the transition's own task logic.
- Clients that need a pre-flight answer call the `authorize` function; that is what it exists for.
- The gate cannot be retrofitted as an `ITransitionSpecification`: `IsSatisfiedBy` is synchronous, while
  role evaluation is async (a `$PreviousUser` grant costs a repository read). It would have to sit in
  `TransitionAsync` alongside `ValidateTransitionRequestAsync`.

## The canonical rule

Evaluated over the whole grant set, in this order:

1. **DENY wins.** Any matching DENY grant denies, wherever it appears in the set.
2. **ALLOW match grants.** With no matching DENY, any matching ALLOW grant allows.
3. **A set with no ALLOW grant is a blacklist** — allowed unless explicitly denied.
4. **An empty set is allowed.** No grants means no restriction.

Multi-role callers are evaluated with *any allowed role grants access*. A caller with no roles is still
evaluated once, so predefined and dynamic grants apply to them.

## Grant forms

| Form | Example | Resolved against |
|---|---|---|
| Static | `backoffice.operator` | caller roles, `OrdinalIgnoreCase` |
| Predefined — actor | `$InstanceStarter`, `$PreviousUser` | `ICurrentUser.ActorUserName` vs `Instance.CreatedBy` / last manual transition's `CreatedBy` |
| Predefined — behalf-of | `$InstanceBehalfOfStarter`, `$PreviousBehalfOfUser` | `ICurrentUser.UserName` vs `Instance.CreatedByBehalfOf` / last manual transition's `CreatedByBehalfOf` |
| Dynamic | `$role.$.context.Headers.x-branch`, `$user.$.context.Instance.Data.ownerId` | value resolved from the authorization context |

**CreatedBy pairs with the actor; BehalfOf pairs with the user name.** Getting this backwards is the
classic bug — see `DynamicRoleGrantTests` and `TransitionAuthorizationManagerBehalfOfTests`.

Predefined and dynamic grants are matched **on the grant side**, independent of which caller role is
being evaluated. That is what makes `[deny: $InstanceStarter]` bind to the instance starter no matter
what other roles they hold.

Dynamic grants are validated at definition time by `DynamicRoleGrant.Classify`. A malformed dynamic
grant falls through to static comparison and becomes **silently inert** — an ALLOW that never grants,
a DENY that never denies. Never re-implement the parse rules; call `Classify`.

## One evaluator, one decision

All instance-bound evaluation funnels through `IRoleGrantEvaluator`, created by
`ITransitionAuthorizationManager.CreateEvaluatorAsync`. The methods on the manager
(`IsTransitionAllowedForRoleAsync`, `IsRoleAllowedForGrantsAsync`, `IsAnyRoleAllowedForGrantsAsync`,
`IsQueryAllowedAsync`, `FilterAuthorizedTransitionKeysAsync`) are thin wrappers over it.

**Never add a second matcher.** Historically three diverged inside the manager itself, plus a fourth in
the monitor with inverted default semantics, and the surfaces disagreed about the same transition.
`RoleGrantEvaluatorTests` pins the evaluator against `EvaluateRolesStatic` over a grant × role matrix
precisely so this cannot recur.

### Batching

An evaluator is **batch-scoped**: create one, query it many times. It resolves the previous manual
transition at most once, and memoizes the dynamic-role authorization context per transition key —
lazily, so a grant set with no dynamic grant never pays for building it (which serializes the
instance's full latest data).

`CreateEvaluatorAsync` takes `grantsForPrefetchHint`. It must cover **every** grant the evaluator will
be asked about: a `$PreviousUser` / `$PreviousBehalfOfUser` grant evaluated but absent from the hint
can never match. Derive the hint from exactly the grants you will evaluate — the union of a state's
transitions, of a schema's guarded paths, of an instance's candidate transitions.

```csharp
var evaluator = await transitionAuthorizationManager.CreateEvaluatorAsync(
    instance, workflow, requestContext,
    candidates.SelectMany(c => c.Grants), cancellationToken);

var allowed = candidates.Any(c => evaluator.IsAnyRoleAllowed(callerRoles, c.Grants, c.Transition));
```

## Every surface must be given the same request context

Dynamic grants read `$.context.Headers`, `$.context.QueryParameters` and `$.context.RouteValues` from
`AuthorizationRequestContext`. **Omitting the context does not fail closed — it makes those namespaces
empty**, so the grant silently cannot match.

Pass the same context everywhere a grant set is evaluated. Otherwise the surfaces disagree about the
same transition: one guarded by `$role.$.context.Headers.x-branch` vanishes from `availableTransitions`
while the `authorize` function — which does pass the context — answers *allowed* for it. The client is
then told it may act on something it was never offered.

This is about the surfaces that *do* evaluate roles agreeing with each other. Execution is out of scope
by design (see above), so it is never the reference point for whether discovery is correct.

## Caller roles: `ICurrentUser` first, legacy `role` header second

`ChangeFromHeaders` is **not** installed in the HTTP pipeline — it only runs in background execution
scopes (`TransitionRunner`). So every HTTP call site must resolve roles itself:

```csharp
var callerRoles = currentUser.ResolveCallerRoles(headers);
```

`ResolveCallerRoles` prefers `ICurrentUser.Roles` and falls back to the legacy `role` header
(comma- or space-separated). It is a **fallback, not a merge**.

**Never read `currentUser.Roles` directly at a decision point.** A header-only caller would be treated
as role-less: rejected with 403 by an allowlist grant set, or served a body with every guarded field
pruned. The one legitimate direct read is inside a resolution helper that then falls back to the header
(`AuthorizeAppService.GetCallerRoles`).

Caller roles also feed `CallerScopeHash`, which keys the data- and schema-function caches. The role set
used for the authorization decision, for field filtering, and for the cache key must be the *same* set
— otherwise one cache entry gets filled with differently-filtered bodies.

### Deliberate system-identity reads

Some reads intentionally run as the system, not the caller, and skip `queryRoles` and `x-roles`
entirely:

- `GetInstanceDataTaskExecutor` — a workflow task reading another instance.
- Related-instance access from scripts (`context.Related`) — see
  [Related Instance Access](../runtime/script-related-instance-access.md).

Copying a field read this way into instance data makes it visible to callers the grants would otherwise
have filtered it from. Document it where you copy it.

## Behavior changes in 0.0.79

Three long-standing divergences were closed. Domains using the affected features should re-check their
expectations:

1. **`x-roles`: DENY now really wins.** Previously each caller role was evaluated against a path's
   grants in isolation, so `[deny: $InstanceStarter]` was defeated by the caller holding any other
   role — the blacklist fallback re-opened the field. The field is now hidden. *More restrictive.*
2. **`x-roles`: a role-less caller now sees deny-only fields.** The role-less caller used to be
   rejected before the blacklist rule applied. Canonical rule 3 now applies. *More permissive.*
3. **`x-roles` honors predefined and dynamic grants at runtime.** Predefined roles previously worked
   only via a caller-side synthesis trick; dynamic grants were silently inert. Both now resolve
   normally.
4. **Human-task list agrees with transition execution.** The list used to require a static match **and**
   a predefined match, so `[allow: teller, allow: $InstanceStarter]` demanded both. Any matching ALLOW
   now suffices, and dynamic grants are honored. A user who could execute a transition but did not see
   the task in their list now sees it. *More permissive.*

## Related

- [Well-Known Transitions](well-known-transitions.md) — how `cancel` / `updateData` / `exit` roles are enforced.
- [API and Service Contracts](../contracts/api-and-service-contracts.md) — internal-only endpoints with no in-app authorization.
- `.claude/rules/vnext-workflow-developer.md` § Role Grant Validation — definition-time rules.
