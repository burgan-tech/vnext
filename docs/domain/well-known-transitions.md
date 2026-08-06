# Well-Known Transitions (`cancel`, `updateData`, `exit`)

## Purpose

A workflow declares three optional **workflow-level** transitions alongside its states and shared
transitions:

| Field | Well-known request alias | Pipeline behavior |
|-------|--------------------------|-------------------|
| `cancel` | `cancel` | `HandleCancelPreflightStep` (5) short-circuits to `CreateTransition`; `HandleFinishStep` (100) calls `Instance.Cancel(...)`. |
| `exit` | `exit` | Same preflight path as cancel; `HandleFinishStep` calls `Instance.Complete(...)`. |
| `updateData` | `update-parent-data` | `HandleUpdateDataPreflightStep` (9) writes the transition record and merges data, then skips to `Finalize` — **only while the instance's current state is a `SubFlow` state**. Target is fixed to `$self`. |

They are declared once on the workflow rather than per state, and `WellKnownTransitionSpecification`
exempts them from `StateTransitionListSpecification`, so they are callable from any state without
being listed in that state's `transitions` array.

All three deserialize into the same `Transition` type (`BBT.Workflow.Definitions.Workflow.Cancel` /
`.UpdateData` / `.Exit`), so they carry the full transition surface: `labels`, `view`, `schema`,
`mapping`, `onExecutionTasks`, `annotations` and — the subject of this document — `roles`.

## Client discovery: they appear in `availableTransitions`

All three are listed by the State function alongside state and shared transitions, so the vNext
Client Workflow Manager SDK discovers and drives them through the same loop it uses for every other
transition — no hard-coded client knowledge of the well-known keys.

`Workflow.GetAvailableUserTransitionKeys(currentState)` appends each of the three when it is:

- configured on the workflow, **and**
- declared with `triggerType` Manual (0) or Event (3), **and**
- allowed in the current state by its `availableIn` constraint — empty/absent means every state,
  the same semantics as shared transitions (see [§ `availableIn`](#availablein-per-state-availability)).

When the instance sits in a `SubFlow` state, `MergeWithParentAvailableTransitions` merges the
**parent's** `cancel`, `updateData` and `exit` into the subflow's own transition list. This is the
primary surface for `updateData`: its preflight step only does work in exactly that situation.

### The configured key is what gets listed

The list carries the **configured** key, not the well-known alias:

```json
"exit": { "key": "leave-process", "target": "exited", "triggerType": 0, "labels": [ ... ] }
```

surfaces as `"leave-process"`, never as `"exit"`. This matters because role filtering resolves each
key through `Workflow.FindTransitionInContext`, which matches these three on their configured key.
Listing the alias instead would make the filter drop the key silently.

Clients should therefore always call back with the key they were given. The aliases remain accepted
on the request side (`Workflow.ResolveWellKnownKey`) for callers that do not read the state response.

### Response shape

Each entry is a `TransitionItem` with a `kind` discriminator drawn from the workflow-definition
field names:

| `kind` | Source |
|--------|--------|
| `stateTransition` | current state's `transitions[]` |
| `sharedTransition` | workflow `sharedTransitions[]` |
| `cancel` / `exit` / `updateData` | workflow-level well-known transitions |
| `timeout` | workflow `timeout` |

Note `updateData` — the kind deliberately mirrors the JSON field name, **not** the
`update-parent-data` request alias.

`view`, `schema` and `annotations` are resolved from the transition definition exactly as they are
for a state transition, so a well-known transition can carry its own modal/popup view and input
schema.

## `availableIn`: per-state availability

`availableIn` restricts which states a transition is offered in. It applies to `sharedTransition` and
to all three well-known transitions; empty or absent means **every state**.

Each item is either a bare state key or an object that additionally narrows that state by role. The
two forms may be mixed in one array:

```json
"availableIn": [
  "review",
  { "state": "approval", "roles": [ { "role": "backoffice.supervisor", "grant": "allow" } ] }
]
```

An entry with no `roles` is exactly equivalent to the bare string form, so definitions authored before
per-state role scoping behave identically — this is why the extension is not a breaking change.
`AvailableInJsonConverter` writes each entry back in the shape it was authored (role-less ⇒ string),
so component JSON round-trips unchanged.

### Role composition is AND

`transition.roles` is the **global gate**; an entry's `roles` is an **additional narrowing** that
applies only in that state. A caller must satisfy **both**:

| `transition.roles` | `availableIn[state].roles` | Result |
|---|---|---|
| allows | allows | allowed |
| allows | denies | **denied** |
| denies | allows | **denied** |
| allows | absent/empty | allowed (legacy behavior) |
| absent/empty | allows | allowed |

Each level is evaluated independently by the shared `IRoleGrantEvaluator`, so DENY-wins,
allowlist-vs-blacklist, and predefined/dynamic resolution behave the same at both levels. Because an
empty grant set is allowed, the AND degrades cleanly.

> **Both levels must be in the prefetch hint.** `CreateEvaluatorAsync`'s `grantsForPrefetchHint` must
> cover the per-state grants too — a `$PreviousUser` grant that lives only on an `availableIn` entry
> can never match if it was not in the hint.

### First match wins

If a definition lists the same state twice, `Transition.FindAvailableIn` takes the first entry.
`WorkflowValidator` reports duplicates as errors, because a second entry is silently dead — and if it
is the one carrying the role narrowing, the restriction never applies at all.

State keys are compared **Ordinal** (they match `^[a-z0-9-]+$`).

## Authorization

`roles` on `cancel`, `updateData` and `exit` is enforced, using the same `RoleGrant` evaluation as
every other transition (`TransitionAuthorizationManager`: DENY wins; any ALLOW makes the set an
allowlist; a deny-only set is a blacklist; `$InstanceStarter` / `$PreviousUser` pseudo-roles and
`$user` / `$userBehalfOf` / `$role` dynamic roles all apply).

Enforcement points:

- **State function** — `availableTransitions` is filtered per caller, so an unauthorized caller
  never sees the key. While in a subflow, parent-owned keys are filtered against the **parent's**
  grants (the subflow already filtered its own).
- **`/functions/authorize`** and **`/functions/authorization-matrix`** — evaluate and report these
  transitions.
- **Subflow overrides** — a parent's `subFlow.overrides.transitions[key].roles` replaces the
  subflow's grants for that key, as for any other transition. No `availableIn` narrowing is applied
  on that path: those overrides key off the *subflow's* transitions, so the parent's `availableIn`
  states would not apply to them.

### What each surface checks

`availableIn` and `roles` are enforced by three different surfaces, and they no longer disagree:

| Surface | `availableIn` state | Roles (transition + per-state) |
|---------|:-------------------:|:------------------------------:|
| State function `availableTransitions` | Yes | Yes |
| `/functions/authorize` | Yes | Yes |
| Transition execution (`POST .../transitions/{key}`) | Yes | No |

Both role-aware surfaces funnel through
`ITransitionAuthorizationManager.IsTransitionAllowedInStateAsync` / `FilterAuthorizedTransitionKeysAsync`,
so a transition offered by one is accepted by the other. `TransitionAuthorizationManagerAvailableInTests`
pins that equivalence.

> **Roles are not enforced at execution time.** `POST .../transitions/{key}` validates the schema and
> the state machine — including the `availableIn` **state** gate — but does not re-check role grants.
> This is the existing behavior for *every* transition type, not a gap specific to the well-known
> three: role grants scope what a client is *offered*, and a caller that constructs the request by
> hand can still execute it. Treat `roles` as UI/discovery scoping, and put genuine authorization in
> the network perimeter or in transition tasks.
>
> The state gate **is** enforced there. Until 0.0.80 it was not: `WellKnownTransitionSpecification`
> returned `Result.Ok()` unconditionally and `StateTransitionListSpecification` excluded these keys,
> so `cancel` / `updateData` / `exit` could be POSTed from any state regardless of `availableIn`.
> Executing one outside its `availableIn` now fails with `Transition:100024`.

### Side effect on instance list queries

`FilterAuthorizedInstancesAsync` keeps an instance in a list result when the caller is authorized
for at least one available transition. Since `exit` and `updateData` now count, an instance whose
only caller-authorized transition is a role-less `exit` becomes visible where it previously was not.
This already applied to `cancel`; it is accepted for consistency.

## Definition-time validation

`WorkflowValidator` puts all three through `ValidateSingleTransition`, which covers:

- **Role grants** — see § Role grant validation below.
- **Trigger-type rules** — Manual/Event transitions may not carry `rule` or `timer`.
- `availableIn` is permitted (same as shared transitions): every entry must name an existing state, no
  state may be listed twice, and each entry's `roles` goes through the same dynamic-role syntax check.

`updateData` additionally must declare `target: "$self"`.

### Role grant validation

A `roleGrant.role` takes one of three forms, and only the third is validated:

| Form | Example | Validated? |
|------|---------|------------|
| Static role name | `backoffice.operator` | No — free-form, matched case-insensitively against the caller's role |
| Predefined instance role | `$InstanceStarter`, `$PreviousUser`, `$InstanceBehalfOfStarter`, `$PreviousBehalfOfUser` | No — matched by exact name |
| Dynamic context reference | `$user.$.context.<path>`, `$userBehalfOf.$.context.<path>`, `$role.$.context.<path>` | **Yes** |

A grant is treated as dynamic-role *intent* as soon as it opens with a `$user.` / `$userBehalfOf.` /
`$role.` qualifier prefix. From there the remainder must be the literal `$.context.` — **case
sensitive** — followed by a non-empty navigation path. Anything else is a validation error:

| Value | Error |
|-------|-------|
| `$user.customer` | invalid path, must start with `$.context.` |
| `$user.$.Context.ownerId` | same — the literal is case-sensitive |
| `$role.$.context.` | empty navigation path |

Why this is strict: `DynamicRoleGrant.TryParse` — the gate the runtime uses — compares `$.context.`
with `Ordinal` and returns `null` on any deviation. At evaluation time a `null` parse falls through
to the **static** role comparison, which a value like `$user.customer` can never satisfy, so the
grant is silently inert: an ALLOW that never grants, or (in a deny-only set) a DENY that never
denies. Failing at definition time is the only place this is visible.

Note that a bare `$user` or `$role` with no trailing dot is *not* dynamic-role intent — it is a
static role name to both the validator and the runtime.

Classification lives in `DynamicRoleGrant.Classify`, which reuses `TryParse`'s own prefix constants
and comparisons, so validation and evaluation cannot drift. `DynamicRoleGrantTests` pins the
invariant: `Classify(role) == WellFormed` exactly when `TryParse(role) != null`.

## Schema

`vnext-schema` → `schemas/workflow-definition.schema.json`, definitions `cancelTransition`,
`exitTransition`, `updateDataTransition`. All three pin `triggerType` to const `0` (manual) and
accept:

- `roles` — array of `roleGrant`, identical to every other transition type.
- `availableIn` — shared `#/definitions/availableIn`, identical to `sharedTransition`. Added in 0.0.79
  to close a schema/runtime gap: the runtime had always honored `availableIn` on these three (and the
  docs' capability matrix listed it as optional), but the definitions omitted the field while declaring
  `additionalProperties: false`, so authoring it was rejected and the gate was unreachable. Extended to
  the object form in 0.0.80.

All three are `additionalProperties: false`, so anything not listed in the definition is still
rejected.

## Key implementation files

| Concern | File |
|---------|------|
| Definition + availability | `src/BBT.Workflow.Domain/Definitions/Workflow.cs` (`GetAvailableUserTransitionKeys`, `GetCancelTransitionKey` / `GetUpdateDataTransitionKey` / `GetExitTransitionKey`, `FindTransition`, `ResolveWellKnownKey`, `ResolveWellKnownTransition`, `IsWellKnownTransitionKey`) |
| `availableIn` shape | `src/BBT.Workflow.Domain/Definitions/Transitions/AvailableInEntry.cs`, `AvailableInJsonConverter.cs`; `Transition.IsAvailableInState` / `FindAvailableIn` |
| Aliases | `src/BBT.Workflow.Domain/Definitions/Transitions/WellKnownTransitionKeys.cs` |
| State response + kinds | `src/BBT.Workflow.Application/Instances/InstanceQueryAppService.cs` (`MergeWithParentAvailableTransitions`, `ResolveTransitionKind`) |
| Role evaluation (AND) | `src/BBT.Workflow.Application/Authorization/TransitionAuthorizationManager.cs` (`IsTransitionAllowedInStateAsync`, `FilterAuthorizedTransitionKeysAsync`) |
| Authorize / matrix | `src/BBT.Workflow.Application/Authorization/AuthorizeAppService.cs` |
| Validation | `src/BBT.Workflow.Domain/Definitions/Validators/WorkflowValidator.cs` |
| Pipeline steps | `.../Pipeline/Steps/HandleCancelPreflightStep.cs`, `HandleUpdateDataPreflightStep.cs`, `HandleFinishStep.cs` |
| State-machine exemption + state gate | `src/BBT.Workflow.Domain/Definitions/Specifications/WellKnownTransitionSpecification.cs`, `SharedTransitionAvailabilitySpecification.cs`, `SubFlowBypassSpecification.cs` |
