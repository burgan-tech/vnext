# Role Grant Authorization

Every authorization surface in vNext evaluates the same thing: a **grant set** (`RoleGrant[]`) against
the **caller's roles**. The grant sets differ by surface, the rule does not.

| Surface | Grant set | Enforced at |
|---|---|---|
| Custom function call | `function.roles` | **Not enforced.** `FunctionAccessPolicy` checks scope only; `function.roles` is evaluated by `authorize` alone |
| Built-in `state` / `view` / `data` / `schema` / `master` / `tasks` / `actions` / `incidents` | state `queryRoles`, else workflow `queryRoles` | `InstanceQueryAppService.IsInstanceQueryAllowedAsync` → 403 `QueryAccessDenied` — **behind the in-process enforcement switch** |
| `availableTransitions` discovery | `transition.roles` | `FilterAuthorizedTransitionKeysAsync` (filtered out, not rejected) |
| `functions/authorize` | any of the above, by target | `AuthorizeAppService` → 403 |
| Human-task list | leaf state `queryRoles` (stamped parent override first) | `HumanTaskLeafResolver` (dropped from the list; **fail-closed** — an undeclared `queryRoles` drops the leaf) |
| Schema field visibility | schema `x-roles` per property path | `SchemaFieldFilterService` (field pruned from the body) |
| Long-poll acknowledge | `state.interaction.longPoll` — `roles` **or** one `rule` | `InstanceCommandAppService` → 403 `LongPollAckAccessDenied` — **behind the in-process enforcement switch** |

Two rows above were wrong for several releases and are corrected here: custom-function calls stopped
being role-gated when `FunctionAccessPolicy` was reduced to scope enforcement (`WorkflowErrors.FunctionAccessDenied`
has had no production caller since), and the human-task list reads `queryRoles`, not `transition.roles`.

## `queryRoles` is enforced at the gateway, not in this runtime

`queryRoles` is fully alive as a **definition** and as an **answer**: it is still evaluated, in full
and per hop down the active-correlation chain, by `GET .../functions/authorize?queryRoles=true`. What
this runtime no longer does is evaluate it a *second* time on its own read path.

The deployment target is an Internal Gateway that introspects the caller and consults that `authorize`
function before forwarding. Enforcing again in process is a second decision point on the same
question, and two decision points drift — this repository has already paid for that twice, once when
`authorize` and the state function gave opposite verdicts about the same leaf, and once when a
parent's narrowing silently stopped applying. One question, one answer, one place.

Concretely, the following no longer refuse in process and no longer consult the gate:

| Surface | Who decides now |
|---|---|
| `state`, `data`, `view`, `schema`, `master` | Internal Gateway → `authorize?queryRoles=true` |
| `tasks`, `actions`, `incidents`, `incidents/active` | same |
| `POST .../longpoll/ack` | Internal Gateway → `authorize?ack=true` |

`authorize?ack=true` is what makes the acknowledge case work at all: the interaction's `rule` arm is a
C# script that no gateway can evaluate itself, and that target admits through the very same
`ILongPollInteractionGate` the endpoint used to call. The gate is still in service; only its caller
changed.

**Role RESOLUTION is untouched, and that distinction is the whole point.** `availableTransitions`
filtering, state aliases, `x-roles` field filtering, the human-task list and `CallerScopeHash` cache
keying all still resolve and evaluate the caller's roles. Visibility stayed; enforcement left.

**Consequence a definition author must know:** a runtime deployed without that gateway in front of it
does not refuse these reads. `queryRoles` describes what a caller should be shown and is the answer a
gateway admits on — it is no longer, by itself, a boundary this process defends.

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
- The **state** half of `availableIn` *is* enforced at execution, by `TransitionExecutionPolicy`
  (`SharedTransitionAvailabilitySpecification`, `WellKnownTransitionSpecification`). Only the role half
  is discovery-only. So `availableIn` is the one place where a definition can impose a real
  server-side restriction on *where* a transition may run, even though it cannot restrict *who*.
- Anything that must be an actual boundary belongs in `queryRoles` (which **is** enforced, with 403),
  in a function's `roles` (also 403), or in the transition's own task logic.
- Clients that need a pre-flight answer call the `authorize` function; that is what it exists for.
- The gate cannot be retrofitted as an `ITransitionSpecification`: `IsSatisfiedBy` is synchronous, while
  role evaluation is async (a `$PreviousUser` grant costs a repository read). It would have to sit in
  `TransitionAsync` alongside `ValidateTransitionRequestAsync`.

## The canonical rule

Evaluated over the whole grant set **and the caller's whole role set**, as two groups:

```
authorized = DenyGroupOk AND AllowGroupOk

DenyGroupOk  = no deny grant matches ANY of the caller's roles     (AND over the denies)
AllowGroupOk = there are no allow grants at all                    (blacklist)
               OR at least one allow grant matches at least one role  (OR over the allows)
empty grant set → allowed
```

1. **The DENY group is an AND, and it is evaluated first.** Every deny must hold, and a deny holds
   only while nothing the caller carries matches it. One breach refuses outright.
2. **The ALLOW group is an OR.** Any one allow grant matching any one role admits.
3. **A set with no ALLOW grant is a blacklist** — allowed unless explicitly denied.
4. **An empty set is allowed.** No grants means no restriction.

A caller with no roles is still evaluated once, so predefined and dynamic grants apply to them.

### A denied role is not bought back by an allowed one

This is the half people get wrong, and it is the half that changed. The rule used to be applied per
caller role inside a loop that returned on the **first** role that was allowed, so a deny for role B
was never reached once role A had matched an allow — a caller holding `[approver, blocked]` passed.
It no longer does.

| Grant set | Caller roles | Result |
|---|---|---|
| `allow: approver`, `deny: blocked` | `[approver]` | allowed |
| `allow: approver`, `deny: blocked` | `[blocked]` | refused |
| `allow: approver`, `deny: blocked` | `[approver, blocked]` | **refused** |
| `deny: blocked` only | `[other]` | allowed |
| `deny: blocked` only | `[other, blocked]` | **refused** |
| `allow: approver` only | `[other]` | refused |
| `{}` | anything | allowed |

A deny grant is therefore a real block: `deny: $InstanceStarter` expresses four-eyes even for a
caller whose operator role is separately allowed.

### Every surface evaluates the caller's whole role set

Not one of them. `IsAnyRoleAllowed`, `IsRoleAllowedForGrantsAsync`, `IsTransitionAllowedForRoleAsync`,
`IsTransitionAllowedInStateAsync` and `FilterAuthorizedTransitionKeysAsync` all take the set.

Four surfaces used to be fed `ICallerRoleResolver.SingleRoleOf(roles)` — literally `roles[0]` —
namely transition listing, `availableIn` narrowing, the parent's transition override and state
aliasing. Two consequences, both measured on the running lab against a state granting
`ht-approver` and `ht-c-approver`:

```
x-roles: other,ht-c-approver   →  []                              ← the grant was never evaluated
x-roles: ht-c-approver,other   →  [ht-c-approve, cancel-ht-c]
```

The answer depended on the **order** the caller happened to list its roles, and the deny group — an
AND across every role — could not apply at all. `SingleRoleOf` remains only where one role really is
the input: cache scoping (`CallerScopeHash`) and picking a state alias to display.

**Never loop the caller's roles and return on the first allowed one.** That reconstructs the
composition a layer up, and it reconstructs the wrong one. `AuthorizeAppService` had it twice — once
per authorize evaluation and once per grant set — and both had to be collapsed into a single call.

### The gate resolves from the instance's own state

`TransitionAuthorizationManager.IsQueryAllowedAsync` keys its lookup on `Instance.CurrentState`.

It used to key on `EffectiveState`, which is the deepest **active subflow's** state key. On a level
that has a subflow of its own, that names a state belonging to a different workflow: `FindState`
returns null, the override stamped on the child (keyed by the state its parent declared) cannot
match either, and the gate quietly falls through to the workflow root's `queryRoles`. Measured on
the bench — `CurrentState=mid-waiting`, `EffectiveState=leaf-waiting` — a role the root had narrowed
away still read that mid `200`.

The consequence was not a wrong answer in some corner: it was a **written restriction that stopped
applying**, silently, for exactly as long as the child had a subflow of its own, covering both a
SubFlow state's own `queryRoles` and a parent's `overrides.states` narrowing. The human-task
resolver (`HumanTaskLeafResolver`) already resolved its grants from `CurrentState`, so the two paths
had been giving different answers about the same instance; this was the side that was wrong.

Descent is a separate mechanism and is unaffected: a caller is gated at the polled instance and then
gated again at each level beneath it. Reading a descendant's state key at the top was never how the
descent worked — it just made the top's own gate unresolvable.

### `availableIn` is not the state gate for a state transition

A **state** transition carries no `availableIn`; it is scoped to the state that declares it.
`IsAvailableInState`'s "empty means every state" rule is correct for shared and well-known
transitions — the two families `availableIn` exists to describe — and wrong for this one. Read
through it, `approve` (declared only on `review`) answered *allowed* for an instance sitting in
`intake`.

`IsTransitionAllowedInStateAsync` therefore asks whether the key is state-scoped at all
(`IsStateScoped`) and, when it is, requires the current state to declare it. The failure this closes
was permissive: the state function offered `[submit-for-review, record-note, cancel-role-matrix]`
while `authorize?transitionKey=approve` answered 200 for the same caller and instance, and execution
rejected the call with `Transition:100021`. A discovery surface that is too generous is a cosmetic
bug; an **oracle** that is too generous becomes an access-control bug the moment a middle tier
admits on its answer.

### Why deny runs first

Not only because a refusal is the cheaper answer. Matching an **allow** is the side that resolves
predefined and dynamic grants, and a dynamic grant's context build serializes the instance's full
latest data. Evaluating the deny group first means a refused caller never pays for it.

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

Note that a transition can contribute **two** grant sets: its own `roles` and the `roles` on the
`availableIn` entry matching the current state. Both are evaluated, so both belong in the hint —
`FilterAuthorizedTransitionKeysAsync` unions `Transition.Roles` with `FindAvailableIn(state)?.Roles`
for exactly this reason.

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

### The `morph-idm` provider replaces that resolution entirely

`CallerRoleProvider:Provider` selects where caller roles come from, once at startup, process-wide.
With `default`, the above applies. With `morph-idm`, the roles are the operation set the identity
service answers for the caller, and **the `role` header decides nothing**.

Three rules, each one a place the provider would otherwise quietly stop being the authority:

- **The `role` header is never forwarded.** The endpoint has two modes: asked *without* a role it
  returns the caller's whole operation set, asked *with* one it degenerates into a yes/no check for
  that single role. The runtime needs the set — grants, `availableIn` narrowing, `queryRoles` and
  `x-roles` are all evaluated against it — so forwarding the header would silently reduce every
  answer to one role, with no error and no log.
- **The answer is never merged with the header.** Only the service's roles are valid. A merge would
  let a gateway-asserted header widen what the identity service governs.
- **And the `role` request parameter is ignored too.** `authorize` accepts a `role` query parameter
  for probing a single role; under this provider it buys nothing
  (`ICallerRoleResolver.AllowsRoleParameterFallback` is false). Without that rule the header hole
  simply reappeared on the query string: a `204` answer was overridden by the caller naming its own
  role. Pinned by `AuthorizeRoleParameterFallbackTests` and, end to end, by the chain lab.
- **`204` is an empty set, not an absence.** It is the shape that invites the mistake: a successful
  response carrying no roles. Read as "nothing to say, use what you have", it restores the header as
  a fallback and a caller can name its own roles. A transport failure or a 5xx is the opposite case —
  a resolution **failure** (403 `Authorization:CallerRoleResolutionFailed`), never an empty set:
  empty and unknown look identical one line later and mean opposite things, and an unreachable
  identity service read as "no roles" turns every blacklist grant set into a silent blanket *allow*.

Identity travels on `AetherClaimTypes` headers — `sub`, `act_sub`, `position`, `client_id` — resolved
from `ICurrentUser` first and from the forwarded header dictionary second (background scopes have no
ambient HTTP request). Pinned by `MorphIdmCallerRoleResolverContractTests` and, end to end, by
vnext-example's `AuthorizationChainLab/MorphIdmProviderTests`.

An unrecognized provider name degrades to `default` rather than failing startup: a typo costs a
role-resolution strategy, not a boundary.

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
