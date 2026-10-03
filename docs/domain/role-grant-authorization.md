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
5. **A caller with no roles cannot clear a role-bound deny.** A static role (`blocked`) or a
   `$role.$.context…` reference is a statement about the caller's *roles*; with none to compare,
   "nothing matched" is not evidence that the caller is not the denied one, so the deny refuses.
   Identity-bound denies — the four predefined roles and `$user.` / `$userBehalfOf.` — match on the
   caller's identity, not its roles, and keep their normal evaluation. Stated precisely this is the
   three-valued rule of [Combinators](#combinators-allof--anyof): a role-bound leaf is *Unknown* for a
   role-less caller, and a deny fires on Yes **or** Unknown.

A caller with no roles is still evaluated once, so predefined and dynamic grants apply to them.

Rule 5 exists because a role-less caller is not rare: an anonymous or device token, a token minted by
a process that carries no roles, and — under `morph-idm` — any caller whose operation set could not be
fetched (see below) all arrive with an empty set. Read as a pass, every blacklist became a blanket
allow for all of them: the grant author wrote a refusal and the runtime waived it. The rule applies
to every provider and every surface, because it lives in the one evaluator
(the Kleene rule in `RoleGrantMatcher`, used by both `RoleGrantEvaluator` and
`EvaluateRolesStatic`).

| Grant set | Caller roles | Result |
|---|---|---|
| `[deny: blocked]` | `[teller]` | allowed (blacklist) |
| `[deny: blocked]` | none | **refused** (rule 5) |
| `[deny: $InstanceStarter]` | none, caller is not the starter | allowed (identity-bound) |
| `[allow: $InstanceStarter, deny: blocked]` | none, caller is the starter | **refused** (rule 5) |
| `[allow: teller]` | none | refused (allowlist, nothing matched) |

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

## Combinators (`allOf` / `anyOf`)

A grant is normally `{ "role": "...", "grant": "allow|deny" }`. To say "the caller is the starter **and**
holds `branch-manager`" — one condition made of several — a grant may carry **exactly one** of `role`,
`allOf` or `anyOf`. The plain form is unchanged and gives the same result as before.

```json
{ "allOf": [ { "role": "$InstanceStarter" }, { "role": "branch-manager" } ], "grant": "allow" }
{ "anyOf": [ { "role": "$InstanceStarter" }, { "role": "$InstanceBehalfOfStarter" } ], "grant": "allow" }
```

Shape rules, enforced at publish (`RoleGrantDefinitionRules`) and when a schema's `x-roles` is parsed:

- `role` XOR `allOf` XOR `anyOf`; `grant` stays on the outer grant.
- Children are `{ "role": "..." }` only — no `grant`, no nesting (**depth 1**). A child may be a static,
  predefined or dynamic role; dynamic children are validated like any dynamic grant.
- A combinator needs at least one child, and an unknown member on a child is rejected rather than
  ignored (a typo would otherwise make the child silently inert). Inside a schema's `x-roles` an
  unparseable entry (bad shape, extra child member) is skipped by `SchemaRolesParser` instead, as before.
- There is no second matcher: a combinator is evaluated by the same `RoleGrantMatcher` as a plain grant.

### Three-valued evaluation

A condition does not always have a yes/no answer. A **role-bound leaf** (a static role or a `$role.`
reference) is a statement about the caller's *roles*; for a caller with no roles it is neither
proven nor disproven, so it is **Unknown**. **Identity leaves** — the four predefined roles, `$user.`
and `$userBehalfOf.` — compare the caller's identity, so they are always **Yes** or **No**. On the
no-instance path (no instance to read the starter or history from) an identity leaf is **No**.

| Leaf | Caller has roles | Caller has no roles |
|---|---|---|
| static role / `$role.…` | Yes if matched, else No | **Unknown** |
| `$InstanceStarter`, `$PreviousUser`, `$InstanceBehalfOfStarter`, `$PreviousBehalfOfUser`, `$user.…`, `$userBehalfOf.…` | Yes / No | Yes / No |

Combinators fold their children (Kleene):

| `allOf` children | Result | | `anyOf` children | Result |
|---|---|---|---|---|
| any **No** | No | | any **Yes** | Yes |
| else any **Unknown** | Unknown | | else any **Unknown** | Unknown |
| all **Yes** | Yes | | all **No** | No |

### Set decision

The canonical rule above is unchanged; only "matches" became three-valued:

- a **deny** grant fires on **Yes or Unknown** — one that cannot be ruled out refuses;
- an **allow** grant admits only on **Yes**;
- an empty set allows; a set of denies only (a blacklist) allows when no deny fired.

So the role-less-caller rule 5 is just this: every role-bound deny is Unknown for them, so it fires. An
`allOf` deny that contains an identity leaf can rule itself out (identity No ⇒ the `allOf` is No), which
is what makes the examples below work for role-less callers too.

### Examples

| Intent | Grant set |
|---|---|
| Customer: the starter who also holds `customer` | `allow allOf[$InstanceStarter, customer]` |
| Corporate: either of two profiles (OR of two ANDs) | `allow allOf[$InstanceStarter, corporate-user]`, `allow allOf[$InstanceBehalfOfStarter, corporate-user]` |
| Starter or the person they act for | `allow anyOf[$InstanceStarter, $InstanceBehalfOfStarter]` |
| Four eyes: the maker acts, but not twice | `allow maker`, `deny allOf[maker, $PreviousUser]` |
| Blacklist: block a role only for its own starter | `deny allOf[blocked, $InstanceStarter]` |

The four-eyes pair means: whoever holds `maker` is allowed, *unless* they hold `maker` **and** were also
the previous user. A caller without `maker` cannot satisfy the `allOf`, so the deny does not fire for them
(No dominates), and they are refused by the allow side only.

### Dynamic path spelling

A dynamic child (and a plain dynamic grant) is `<qualifier>$.context.<path>` — the `$.context.` part is
literal and **case-sensitive**. Anything else with a qualifier prefix is a publish error.

| Spelling | Valid? | Why |
|---|---|---|
| `$user.$.context.Instance.CreatedBy` | valid | the starter, compared with the user |
| `$userBehalfOf.$.context.Instance.CreatedByBehalfOf` | valid | the behalf-of starter |
| `$user.$.context.Instance.Data.<field>` | valid | a field inside the instance data |
| `$user.$CreatedBy` | **invalid** | no `.context.` — becomes inert if it slipped through |
| `$user.$context.Instance.CreatedBy` | **invalid** | missing the `.` after `$` |
| `$user.$.context.Instance.Data.CreatedBy` | wrong | resolves, but `CreatedBy` is an instance property, **not** inside `Data` — the grant never matches |

### Predefined roles resolve against the grant's own instance (K8)

`$InstanceStarter` and `$PreviousUser` (and their behalf-of twins) are read from the instance **the grant
is evaluated for** — also when the grant is a parent-stamped override running on a child. A parent's
override naming `$InstanceStarter` therefore means the *child's* starter, not the parent's.

### Where combinators are not accepted (K1)

`x-masking` and `x-encryption` exemption lists take plain `{ "role", "grant": "allow" }` entries only
(`RoleGrantDefinitionRules.ValidateExemption` rejects a combinator at publish). They are an allow-list of
who sees a value in the clear; a three-valued fold has no place there. `x-roles` field visibility,
`transition.roles`, `queryRoles`, `availableIn.roles`, function `roles` and subflow overrides all accept
combinators.

### The one behavior change

A **role-less caller against an ALLOW grant of a `$role.` reference** used to resolve the reference to `""`
and could be admitted by an allow that compared against nothing. A role-bound leaf is now Unknown, and an
allow admits only on Yes, so that caller is **no longer admitted**. Denies were already refusing role-less
callers (rule 5) and are unchanged; plain static and predefined grants evaluate exactly as before.

### In the authorization matrix

`functions/authorize` returns the grants it evaluated. A combinator is shown as
`{ "allOf": [ { "role": "..." } ], "grant": "..." }` (or `anyOf`) with **`role` omitted** on the grant;
a plain grant keeps `{ "role", "grant" }`. A client reading the matrix must tolerate a grant without `role`.

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

### The `morph-idm` provider

`CallerRoleProvider:Provider` selects where caller roles come from, once at startup, process-wide.
With `default`, the above applies. With `morph-idm`, **the request's `role` header takes precedence,
and only a request without one is sent to the identity service** (committee decision, 2026-09-25):

| Request | Role set | morph-idm called? | `vnext.auth.outcome` | Log |
|---|---|---|---|---|
| carries a non-blank `role` header | the header's roles — **replaces** the service's answer, no merge | **no** | `header` | Debug 20465 |
| carries no `role` header (or only a blank one) | the service's operation set — or `[]` on any failure, see below | yes | `resolved` / `empty` / `failed` / `skipped` | see the table below |

"The `role` header" is read exactly as the default provider reads it: `ICurrentUser.Roles`, which the
framework parses from the header, else the forwarded header dictionary in a scope with no HTTP
request. This replaced the 2026-09-22 rules that the header decides nothing and is never merged:
a caller whose request asserts roles is now evaluated with those roles alone.

The rules that still hold:

- **The `role` header is never forwarded.** The endpoint has two modes: asked *without* a role it
  returns the caller's whole operation set, asked *with* one it degenerates into a yes/no check for
  that single role. With the precedence rule a request that carries the header makes no call at all,
  so this can only matter if that rule is removed — keep both.
- **`authorize`'s `role` query parameter behaves like the header.** Under this provider
  (`ICallerRoleResolver.RoleParameterMode` = `AsRoleHeader`) the parameter is handed to the resolver
  as the request's `role` header when the request carries none — so it is the role set and morph-idm
  is not asked. A real `role` header wins over it. This applies to every target, `ack` included.
  Before 2026-09-25 the parameter was ignored under morph-idm; once the header became decisive, the
  same claim answered 200 through the header and 403 through the query string, which is what this
  removes. Pinned by `AuthorizeRoleParameterFallbackTests` and, end to end, by the chain lab.
- **`204` is an empty set, not an absence.** Only reached for a request without a `role` header, so
  there is nothing to fall back to; it resolves to `[]`.
- **Every other failure is an empty set too — and never breaks the request.** The resolver does not
  fail. For a request without a `role` header each case resolves to `[]` and the request is
  evaluated on it:

  | Case | Log | `vnext.auth.outcome` | Tag that tells it apart |
  |---|---|---|---|
  | neither `act_sub` nor `client_id` (anonymous / device token) — **no call is made** | Debug 20464 | `skipped` | — |
  | `204`, blank body, `roles: []` | Warning 20441 | `empty` | `vnext.auth.empty_reason` = `no_content` / `empty_body` / `empty_array` |
  | non-success status | Error 20442 | `failed` (span Error) | `vnext.auth.failure_kind` = `http_status` + `vnext.auth.provider.status_code` |
  | HttpClient timeout | Error 20442 | `failed` (span Error) | `failure_kind` = `timeout` |
  | connection / DNS / TLS | Error 20442 | `failed` (span Error) | `failure_kind` = `transport` |
  | success status, no recognizable roles array | Error 20463 | `failed` (span Error) | `failure_kind` = `parse` |

  This used to be a 403 (`Authorization:CallerRoleResolutionFailed`) on every surface, on the reading
  that an unknown role set must deny. It was changed (2026-09-24) because an empty set already
  denies what matters: an allowlist grant cannot match it, and **rule 5** makes every role-bound deny
  refuse it. So an outage narrows what a caller sees — fewer transitions, pruned `x-roles` fields, a
  refusal from `authorize` — without breaking reads outright, and it can never widen access. Without
  rule 5 this change would have turned every blacklist into a blanket allow during an outage; the
  two ship together and must not be separated. The outcome is memoized for the scope like any other,
  and every memo-hit span repeats its tags. `Authorization:110004` is no longer produced by either
  built-in provider; the code is kept for a future provider that needs the failure channel.

Identity travels on `AetherClaimTypes` headers — `sub`, `act_sub`, `position`, `client_id` — resolved
from `ICurrentUser` first and from the forwarded header dictionary second (background scopes have no
ambient HTTP request). Pinned by `MorphIdmCallerRoleResolverContractTests` and, end to end, by
vnext-example's `AuthorizationChainLab/MorphIdmProviderTests`.

An unrecognized provider name degrades to `default` rather than failing startup: a typo costs a
role-resolution strategy, not a boundary.

### Trigger-task reads and system-identity reads

- The instance-read trigger tasks — `GetInstanceData`, `GetInstance`, `GetInstances` — read as the header set they
  present: the task's mapping headers plus the pipeline caller's credential — `sub`, `act_sub`, `position`, `client_id`,
  `role` — wherever the mapping did not set them. `x-roles`, `x-masking` and `x-encryption` apply to that caller, the
  same way for a same-domain read (under `ICurrentUser.Change`) and a cross-domain one (headers on the request). `role`
  travels only as the caller sent it: a role morph-idm resolved is never carried — the target resolves it again from the
  forwarded credential.
- Related-instance access from scripts (`context.Related`) still runs as the system and skips `queryRoles`,
  `x-roles` and `x-masking` — see [Related Instance Access](../runtime/script-related-instance-access.md).

**History.** From 2026-09-28 a SERVER-ONLY `SystemRead` flag made same-domain task reads unfiltered while cross-domain
reads went out with no credential at all, so one task answered differently by domain. The flag is deleted.

Copying a field read by a task or through `context.Related` into instance data makes it visible to callers the grants would otherwise
have filtered or masked it from. Document it where you copy it. See [Field Masking](field-masking.md).

## Definition-time validation of dynamic grants

- Three role forms: **static** (`backoffice.operator`), **predefined** (`$InstanceStarter`,
  `$PreviousUser`, `$InstanceBehalfOfStarter`, `$PreviousBehalfOfUser`), **dynamic**
  (`$user.` / `$userBehalfOf.` / `$role.` + `$.context.<path>`). Only dynamic is validated; the
  other two are free-form.
- A qualifier prefix ⇒ dynamic *intent*. The remainder must be the literal `$.context.`
  (**Ordinal — case-sensitive**) plus a non-empty nav path. `$user.customer`,
  `$user.$.Context.x` and `$role.$.context.` are all errors.
- Why strict: `DynamicRoleGrant.TryParse` returns null on any deviation, and runtime `IsMatch` then
  falls through to the **static** comparison — the grant becomes silently inert (an ALLOW that never
  grants, a DENY that never denies). Definition time is the only place it is visible.
- Never re-implement the parse rules in a validator. Use `DynamicRoleGrant.Classify`, which shares
  `TryParse`'s constants and comparisons; the `Classify == WellFormed ⟺ TryParse != null` invariant
  is pinned by `DynamicRoleGrantTests`.

## Evaluator invariants (quick list)

- **Every decision point takes the caller's WHOLE role set, never one of them.** `IsAnyRoleAllowed`,
  `IsRoleAllowedForGrantsAsync`, `IsTransitionAllowedForRoleAsync`, `IsTransitionAllowedInStateAsync`
  and `FilterAuthorizedTransitionKeysAsync` all take `IReadOnlyCollection<string>?`. Feeding one role
  — `ICallerRoleResolver.SingleRoleOf`, which is `roles[0]` — made the answer depend on header ORDER:
  measured on the lab, `x-roles: other,ht-c-approver` was offered nothing while
  `x-roles: ht-c-approver,other` was offered the transition, same caller, same grants. It also put
  the deny group out of reach, since an AND across roles needs the roles. `SingleRoleOf` survives for
  cache scoping (`CallerScopeHash`) and state aliasing display, never for a decision.
- **Every surface evaluating a grant set must be given the same `AuthorizationRequestContext`.** Omitting
  it does not fail closed, it makes `$.context.Headers/QueryParameters/RouteValues` **empty**, so the
  grant silently cannot match — the transition vanishes from `availableTransitions` while the `authorize`
  function, which does pass the context, still answers *allowed* for it.

## Behavior changes in 0.0.99

1. **Role-grant combinators.** Grants may carry `allOf` / `anyOf` (see [Combinators](#combinators-allof--anyof)).
   Purely additive for definitions that do not use them.
2. **A role-less caller is no longer admitted by an ALLOW `$role.` reference** that resolved to `""`.
   *More restrictive, for that one case.*
3. **A subflow decides `queryRoles` at its leaf only** — see [Authorize Function](authorize-function.md);
   the root's `queryRoles` no longer restrict while the instance is inside a SubFlow.
4. **Authorization matrix shape**: combinator grants carry `allOf` / `anyOf` and omit `role`.

## Behavior changes in 0.0.97

1. **`morph-idm`: a request `role` header now decides, and morph-idm is not called.** Before, the
   header was ignored under this provider and only the service's answer counted. A request that
   carries the header is now evaluated with exactly those roles; one without it is resolved through
   the service as before. *A caller asserting roles in the header gets them.*
2. **`morph-idm`: `authorize`'s `role` query parameter behaves like the header.** When the request
   has no `role` header, `?role=X` makes the role set `[X]` and morph-idm is not asked; a real header
   wins over it. Before, the parameter was ignored under this provider.

## Behavior changes in 0.0.96

1. **A role-less caller no longer passes a role-bound deny** (canonical rule 5), on every surface and
   for every provider: `availableTransitions`, `authorize`, `x-roles`, the human-task list, function
   `roles`. This reverses item 2 of the 0.0.79 list below for role-bound denies. *More restrictive.*
2. **`morph-idm` no longer answers 403 when it cannot resolve the caller's roles.** A failure,
   a timeout, an unparseable body and a caller with no `act_sub`/`client_id` all resolve to an empty
   role set and the request is evaluated on it; see the provider section above. *Reads no longer
   fail during an outage; what they return is narrowed by rule 5 and the allowlists.*

## Behavior changes in 0.0.79

Three long-standing divergences were closed. Domains using the affected features should re-check their
expectations:

1. **`x-roles`: DENY now really wins.** Previously each caller role was evaluated against a path's
   grants in isolation, so `[deny: $InstanceStarter]` was defeated by the caller holding any other
   role — the blacklist fallback re-opened the field. The field is now hidden. *More restrictive.*
2. **`x-roles`: a role-less caller now sees deny-only fields.** The role-less caller used to be
   rejected before the blacklist rule applied. Canonical rule 3 now applies. *More permissive.*
   **Reversed in 0.0.96 for role-bound denies** (rule 5).
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
