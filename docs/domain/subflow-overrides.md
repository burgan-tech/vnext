# SubFlow Overrides

A parent that consumes a workflow as a SubFlow (`state.subFlow.type: "S"`) can tune the child for its
own context through `state.subFlow.overrides`, without editing the child.

## What can be overridden

| Path | Effect | Mode |
|------|--------|------|
| `overrides.timeout` | Child's workflow-level timeout | replace |
| `overrides.transitions.<childTransition>.roles` | Child transition's grants | replace (whole list) |
| `overrides.states.<childState>.queryRoles` | Child state's query grants | replace (whole list) |
| `overrides.states.<childState>.interaction.longPoll.fallbackTimeoutSeconds` | Acknowledge window | field-level |
| `overrides.states.<childState>.interaction.longPoll.roles` | Interaction grants | field-level; the list replaces as a whole |
| `overrides.states.<childState>.views.<viewKey>` | Swap the view the child's rules selected in that state | replace reference |
| `overrides.transitions.<childTransition>.views.<viewKey>` | Swap the view the child's rules selected for that transition | replace reference |
| `overrides.views.<viewKey>` / `viewOverrides` | **Deprecated.** Swap by view key everywhere | replace reference, parent-side |

**Never overridable:** rules (view `rule`, long-poll `rule`) and long-poll `terminate`. An override
never adds a long-poll to a child state that declares none.

## Field-level long-poll override

A field you write replaces the child's; a field you leave out keeps the child's.

```json
"overrides": { "states": { "otp-wait": { "interaction": { "longPoll": { "fallbackTimeoutSeconds": 180 } } } } }
```

keeps the child's `terminate` and `roles` and only changes the window. `roles: []` is allowed and
admits every caller; the validator warns. If the child authorizes the interaction with a `rule`, a
`roles` override is ignored (logged, EventId 20306) and a window override still applies.

## How it travels and where it is resolved

`SubflowStarter` stamps `overrides.states` and `overrides.transitions` onto the child at start
(`subflow.state_role_overrides`, `subflow.transition_role_overrides`). The **child** resolves them on
its own `CurrentState` — never `EffectiveState`:

- long-poll: `Instance.ResolveEffectiveLongPoll(state)` — read by the pipeline arm (order 75, the
  fallback job's schedule), the interaction gate (state function + `authorize?ack=true`) and the
  state body's `interaction.fallbackTimeoutSeconds`, so the three always agree;
- views: `Instance.ResolveViewOverride(state, transition, viewKey)` — after the child's own rules
  selected a view. Unresolvable replacement → the child's view is served (EventId 20101).

Because resolution is child-side, overrides also apply when the child is addressed directly. Scope
is one hop: in P → C → G, P's overrides apply to C's states only; G reads C's overrides.

## Limits

- The stamp is a start-time snapshot: children already running keep the overrides they started with.
- The validator cannot see the child definition: an override naming a state the child lacks is
  silently inert; a long-poll override on a state without a long-poll is logged at resolution (20305).
- Do not mix the scoped view overrides with the legacy view map on one subFlow — the validator rejects it.
