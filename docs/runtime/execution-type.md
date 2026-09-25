# Execution Type (SYNC / ASYNC)

`executionType` lets a definition — a flow or an individual transition — declare how a transition
executes, independently of what the caller asks for. vnext#1003.

- **`SYNC`** — the request blocks until the pipeline reaches a rest point and returns the full instance
  (HTTP `200`).
- **`ASYNC`** — the request is accepted and the pipeline runs in the background via the scheduler
  (HTTP `202 Accepted`, `{ id, status }`); the client polls the state function.

It is an optional string enum (`SYNC` / `ASYNC`, upper-case only) on both the **flow** definition
(`attributes.executionType`) and **transition** definitions (state, shared, and start transitions).

## Non-breaking by construction

A flow or transition **without** `executionType` behaves exactly as before: the caller's `sync` query
parameter chooses the mode. The runtime default is **async** — `sync` defaults to `false`, so a request
with neither a definition nor `?sync=true` is accepted as a `202` background job, unchanged from
pre-#1003 behaviour. Existing definitions need no migration.

## Precedence — the definition is the source of truth

When a definition is present it **overrides the caller's `sync` query parameter** — the query parameter
becomes irrelevant for that request. Between the two definition levels, the **inner** wins:

```
effective mode =
    transition.executionType   (the inner definition)   — if set
     else flow.executionType    (the outer definition)   — if set
    else the caller's sync query parameter               — pre-#1003 behaviour
```

So a domain can set the flow to `ASYNC` and still force one specific transition to `SYNC` (or vice
versa) — the transition's setting wins. This is one pure function, `ExecutionModeResolver.Resolve`,
applied at the two caller-driven intake points: the transition context builder
(`InstanceCommandAppService.BuildTransitionContext`, which also serves event-triggered transitions) and
the start context (`ExecuteStartTransitionAsync`, reading `workflow.StartTransition.executionType`). It
sets `WorkflowExecutionContext.Mode` — the **effective** mode the `ExecutionStrategyFactory` keys on to
pick the sync inline pipeline or the async enqueue — while `CallerMode` keeps what the caller asked for.

### Worked example

Flow `ASYNC`, transition `SYNC`, caller sends `?sync=false` (async):

- Effective mode = `SYNC` (the transition, inner, wins). The request **runs synchronously** and returns
  `200` with the full instance.
- The caller *requested* async but got sync — an override. See **Observability** below.

## Scope — all transition requests except automatic

`executionType` governs **client-initiated start**, **manual transitions**, and **event-triggered
transitions** — the requests where a sync/async choice exists and the query parameter used to rule.

It is **not** applied to:

- **Automatic transitions** — they always run inline as part of the auto-chain and never go through the
  sync/async strategy factory. (Excluded by design; there is no meaningful sync/async choice for a hop
  that is part of an already-running chain.)
- **Runtime-internal and timer-fired paths** — scheduled transitions, workflow timeouts, retry, subflow
  resume/cancel/fault, and the long-poll acknowledge. These force their own modes for correctness (a
  subflow resume must stay synchronous so the parent awaits the child, a scheduled fire is inherently a
  background job, …) and are not caller requests, so `executionType` does not alter them.

## Response shape follows the effective mode

The HTTP response reflects what actually ran, not what the caller asked for:

| Effective mode | Response |
|---|---|
| `SYNC` | `200 OK` with the full instance |
| `ASYNC` | `202 Accepted` with `{ id, status }` |

The effective mode travels back to the controller on the internal `InstanceOutputBase.ExecutedAsync`
flag (set from `context.Mode` in `WorkflowExecutionService.BuildTransitionOutput`); the controller shapes
`200` vs `202` from it instead of from the `sync` query parameter.

## Observability — requested vs effective on the trace

When a definition overrides the caller, the divergence is recorded on the existing request/activation
trace span (no new persistence), so an operator can find every override by querying the trace:

| Tag | Meaning |
|---|---|
| `vnext.execution.requested` | the mode the caller asked for (`SYNC`/`ASYNC`, from the query parameter) |
| `vnext.execution.effective` | the mode that actually ran after applying the definition |
| `vnext.execution.overridden` | `true` only when a definition overrode the caller |

This reuses the `Mode` (effective) vs `CallerMode` (requested) split that already existed on the
execution context — before #1003 the two were always equal; now they diverge exactly when a definition
overrides the caller, and the tags make that visible.

## Validation

Only `SYNC` and `ASYNC` are accepted. An unknown value is rejected at deserialization (the
`ExecutionType` value object throws), and the `vnext-schema` `workflow-definition.schema.json` enum
enforces it at authoring time (a reusable `executionType` `$def` referenced from `attributes` and the
transition variants).

**Well-known transitions.** The schema exposes `executionType` on the flow, state transitions,
`sharedTransition` and `startTransition` — deliberately **not** on the well-known `cancel` / `updateData`
/ `exit` transitions. The runtime would honour it there if set (they are plain `Transition` objects), but
authoring it is a `vnext-schema` follow-up, not part of this change. Until then, those transitions fall
back to the flow-level `executionType` (or the caller's query parameter).

## Implementation map

- Value object: `ExecutionType` (`src/BBT.Workflow.Domain/Definitions/ExecutionType.cs`).
- Fields: `Workflow.ExecutionType`, `Transition.ExecutionType` (both optional `[JsonConstructor]` params).
- Resolver: `ExecutionModeResolver.Resolve` / `.IsOverriddenByDefinition`
  (`src/BBT.Workflow.Domain/Execution/Transitions/ExecutionModeResolver.cs`).
- Applied: `InstanceCommandAppService.BuildTransitionContext` (manual + event) and
  `ExecuteStartTransitionAsync` (start); steers `WorkflowExecutionContext.Mode`.
- Response: `InstanceOutputBase.ExecutedAsync`, set in `WorkflowExecutionService.BuildTransitionOutput`,
  read by `InstanceController` (start + transition).
- Telemetry: `TelemetryConstants.TagNames.Execution{Requested,Effective,Overridden}`.
- Schema: `vnext-schema` `workflow-definition.schema.json`.
