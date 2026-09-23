# The `authorize` Function

`GET /api/v1/{domain}/workflows/{workflow}/instances/{instance}/functions/authorize`

The runtime's authorization **oracle**: it answers questions about a caller, it does not guard
anything. Since 0.0.94 it is also the *only* place these questions are answered — the read surfaces
and the long-poll acknowledge endpoint no longer evaluate `queryRoles` or the interaction gate
themselves ([Role Grant Authorization](role-grant-authorization.md#queryroles-is-enforced-at-the-gateway-not-in-this-runtime)).
The Internal Gateway calls this function and admits or refuses on its answer.

That makes one property load-bearing: **`authorize` must never become lenient.** A read surface that
is too generous is a display bug; an oracle that is too generous is an access-control bug the moment
something admits on it.

---

## Request

| Parameter | Where | Required | Meaning |
|---|---|---|---|
| `transitionKey` | query | one of four | May this transition be triggered? |
| `functionKey` | query | one of four | May this **custom** function be invoked? |
| `queryRoles=true` | query | one of four | May this instance be read? |
| `ack=true` | query | one of four | May `POST .../longpoll/ack` be called? |
| `role` | query | no | A single role to probe. **Not** the caller's identity, and ignored entirely under an authority provider — see *Role resolution* |
| `version` | query | no | Pins the workflow definition version; omitted means the instance's own |

**Exactly one of the four selectors** must be present. Zero or two is a request error
(`AuthorizeRequiresExactlyOneTarget`), not a silent default — a caller who forgot the selector must
not receive a verdict about some other question. Enforced in `ValidateAuthorizeTargetInstance`.

Headers, query string and route values are all carried into an `AuthorizationRequestContext`, because
dynamic role grants read `$.context.Headers`, `$.context.QueryParameters` and `$.context.RouteValues`
from it. **Omitting the context does not fail closed** — it makes those namespaces empty, so the grant
silently cannot match.

## Response

| Status | Body | Meaning |
|---|---|---|
| `200` | `{"allowed": true}` | Permitted |
| `403` | `{"allowed": false}` | Refused |
| `4xx`/`5xx` | error envelope | The question could not be answered (see *Failure*) |

**The verdict is in the body on both statuses.** A consumer that reads only the `200` turns every
refusal into "no answer".

---

## What each target actually checks

| Selector | Evaluates | Reads | Descends into an active SubFlow? |
|---|---|---|---|
| `?transitionKey=` | the transition is offered in the instance's **current state**, AND `transition.roles`, AND the `availableIn` entry's roles for that state (composed as **AND**) | `Transition.Roles`, `availableIn[state].roles`, the parent's stamped `subflow.transition_role_overrides` | Only when the parent does **not** retain it |
| `?functionKey=` | the custom function's own `roles` | `Function.Roles` — **no roles defined ⇒ allowed** | Yes |
| `?queryRoles=true` | the state's (or workflow root's) `queryRoles` | the parent's stamped `subflow.state_role_overrides` → the state's own `queryRoles` → the workflow root's | Yes, and the answer is a **conjunction** |
| `?ack=true` | the entered state's `interaction.longPoll` arm — `roles` **or** the condition `rule` | `ILongPollInteractionGate` (the same object the endpoint and the state function's signal emit use) | Follows the endpoint's own rule (`IsAwaitingLongPollAck`) |

### `?transitionKey=` — actionability

The state check comes first and short-circuits, so a transition that is not offered here is refused
without evaluating any role. Two families, two rules, and conflating them was a real defect:

- a **state** transition carries no `availableIn` and is implicitly scoped to the state that declares
  it — the key must be declared on the current state;
- a **shared** or **well-known** transition is scoped by `availableIn`, where an empty list genuinely
  means *every state*.

Reading the first through `IsAvailableInState` made `approve` (declared only on `review`) answer
*allowed* for an instance sitting in `intake`, which execution then rejected with
`Transition:100021`. Permissive, i.e. the direction that matters.

**`transition.roles` is still not enforced at `POST .../transitions/{key}`.** That is deliberate and
unchanged: roles describe what a client should *offer*. This function is where they are evaluated.

### `?functionKey=` — custom functions only

Built-in functions have **no** selector of their own. `state`, `data`, `view`, `schema`, `master`,
`tasks`, `actions` and the incident routes share one `queryRoles` verdict: a caller who may not read
`state` may not read `data` either. A function with no `roles` declared is allowed — absence is not a
denial.

### `?queryRoles=true` — visibility, and it is a CONJUNCTION

The answer is the polled instance's own verdict **AND** every level beneath it, down to the deepest
active leaf. That mirrors what the read path used to do — gate the polled instance, then descend and
gate again — and it is why answering from the leaf alone was wrong: it made the oracle strictly
*weaker* than the thing it describes.

Resolution per level, highest first:

1. the parent's stamped `subflow.state_role_overrides` entry for the instance's **`CurrentState`**;
2. that state's own `queryRoles`;
3. the workflow root's `queryRoles`.

An empty grant set allows. The override **replaces**, it does not merge — a parent that narrowed a
child's visibility meant to narrow it.

Two mistakes this resolution order has already cost real work:

- **It reads `CurrentState`, never `EffectiveState`.** `EffectiveState` is the *deepest active
  subflow's* state key, so on a level that has a subflow of its own it names a state of a different
  workflow — `FindState` returns null, the stamped override cannot match, and the gate falls through
  to the workflow root's grants. A parent's narrowing then stopped applying, silently, for exactly as
  long as its child had a subflow of its own.
- **Overrides are read from the CHILD's stamp, per hop — never from the parent's definition at the
  point of descent.** The parent-side reading returned at depth 1, so a grandchild's own gate never
  ran; and at a *directly addressed* leaf there is no parent in scope at all, so it found nothing and
  answered from the child's own grants — giving the opposite verdict to the same leaf reached through
  its parent.

### `?ack=true` — the acknowledge pre-flight

Admitted through `ILongPollInteractionGate`, which owns the arm selection (`rule`, else `roles`, else
allow). Re-implementing the roles arm here would silently ignore the `rule` arm — and that arm is a
C# script, so it is the one thing a gateway cannot evaluate for itself. This target is what makes
delegating the acknowledge decision possible at all.

It mirrors the endpoint's own descent rule, which is **not** "has a subflow" but "is this the instance
that paused" (`IsAwaitingLongPollAck`). When nothing in the chain is awaiting, the answer is
**allowed**, because the endpoint answers `Ok()` idempotently there; answering `false` would have the
middle tier refuse a call the runtime accepts.

---

## Parent-retained transitions

While a SubFlow correlation is open, four kinds of key are answered against the **parent** and do not
descend — matching execution, so the oracle and the pipeline agree:

| Key | Why it stays with the parent |
|---|---|
| `cancel` | `HandleCancelPreflightStep` (order 5) skips to `CreateTransitionRecordStep` (20), over the forward at order 10 |
| `exit` | same |
| `updateData` | `ForwardToActiveSubflowStep` never forwards it; `HandleUpdateDataDataOnlyStep` (21) writes data and stops |
| a **shared** transition available in the parent's current state | `ForwardToActiveSubflowStep` excludes it |

A shared transition that is *not* available in the current state is not forwarded either — execution
rejects it, and this function denies it on the same `availableIn` check. The two surfaces agree
without sharing code.

---

## Role resolution

The caller's roles come from the configured `CallerRoleProvider` — **not** from `ICurrentUser.Roles`
read at the controller. Reading them there would pin the answer to the default provider's source and
make this function contradict every other surface whenever a different provider is configured
([provider contract](role-grant-authorization.md#the-morph-idm-provider-replaces-that-resolution-entirely)).

The `role` request parameter composes differently per target, and the difference is deliberate:

| Target | `role` parameter | Why |
|---|---|---|
| `transitionKey`, `functionKey`, `queryRoles` | **fallback** — used only when the provider reports no roles at all | a convenience for probing one role; the provider is the authority |
| `ack` | **additive** — merged with the provider's roles | it is how a client names *which* of its roles is acknowledging |

**Both forms are gated on the provider** (`ICallerRoleResolver.AllowsRoleParameterFallback`). The
parameter is honoured only when the provider's own source is already the caller's own assertion —
i.e. the `default` provider, whose roles come from `ICurrentUser` with the `role` header behind it,
so the parameter is the same claim through a different door. Under an **authority** provider such as
`morph-idm` it is ignored outright.

That guard closes a real hole, found by probing after the equivalent header rule was already in place
and green. `authorize` used to fall back to the parameter whenever the provider returned an empty
set, without regard for *which* provider returned it — so morph-idm answering `204` ("this caller has
no operations") was overridden by the caller naming a role in the query string. Measured on the lab,
same caller and same instance:

```
?queryRoles=true                    ->  {"allowed":false}  403
?queryRoles=true&role=chain.admin   ->  {"allowed":true}   200     ← before the guard
```

It is the same hole as forwarding the `role` header to morph-idm, reached through the query string,
and it matters more since this function became the only place these questions are answered: a gateway
that passes the client's query string through would be admitting on the client's own claim. The flag
lives on the resolver rather than on a provider-name check here, so a new provider has to state its
own answer instead of inheriting one.

Evaluation is one call with the **whole** role set, never a loop that returns on the first allowed
role: the deny group is an AND across every role the caller carries, so asking role by role lets an
allowed role answer before a denied one is ever considered. See
[the canonical rule](role-grant-authorization.md#the-canonical-rule).

## Failure

A role-resolution failure (provider unreachable, 5xx, timeout) is a **failure**, not a denial and not
an empty role set: the caller's authority is unknown, and the only safe reading of unknown is a
refusal the consumer can tell apart from a considered `false`.

## Audit

Every decision emits `WorkflowLogs.AuthorizeRequest` (EventId 50030) carrying the domain, workflow,
**instance id**, which of the four questions was asked (`transition:{key}` / `function:{key}` /
`queryRoles` / `ack`), the **resolved** role set and the verdict. The resolved set is logged rather
than the `role` parameter — under an external provider that parameter is usually absent, and logging
it would record an empty role for every decision. Without the target and instance the log records the
same tuple for every call a caller makes against a flow, so one refusal cannot be told from another.

The decision itself is spanned (`Authorization.Decide`); the subflow descent is spanned separately and
**only around the forward**, so "this trace has no `Subflow.Descend`" keeps meaning "nothing
descended".

## Related

- [Role Grant Authorization](role-grant-authorization.md) — grant forms, the canonical rule, the caller-role providers
- [Well-Known Transitions](well-known-transitions.md) — `cancel` / `updateData` / `exit`
- [Long-Poll Termination](long-poll-termination.md) — the `interaction.longPoll` arms the `ack` target evaluates
- [API and service contracts](../contracts/api-and-service-contracts.md) — the route's place among the system functions
