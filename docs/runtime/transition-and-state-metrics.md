# Transition and State Metrics

Two click-to-fetch read endpoints that answer "where is time going in this flow?" for a single
instance (vnext-client-sdk-core#60, item B). They group the already-journaled
`InstanceTransitions` / `InstanceTasks` rows into an **attempts** model that the flat
[`tasks` function](instance-task-and-action-history.md) does not: one attempt per firing (transition)
or per visit (state), each carrying the tasks that ran under it, phase-labelled by
[hook](instance-task-and-action-history.md#hook-vs-triggertype).

They are read-only. No table, column, or write path is added — the prerequisite hook/order columns
landed with the tasks function (`AddInstanceTaskTriggerAndOrderColumns`).

## Endpoints

| Endpoint | Groups by | An attempt is |
| --- | --- | --- |
| `GET /{domain}/workflows/{workflow}/instances/{instance}/transitions/{transitionKey}/metrics` | transition key | one firing (one `InstanceTransitions` row for the key) |
| `GET /{domain}/workflows/{workflow}/instances/{instance}/states/{stateKey}/metrics` | state | one visit (entry→exit) |

`{instance}` accepts the instance id or the business key. Both are ordinary controller routes next to
the [`transitions` history](../architecture/workflow-execution-pipeline.md) and `incidents` reads —
addressed-instance only, no subflow descent, no gateway plumbing. Like every read surface they carry
**no in-process `queryRoles` gate**: the Internal Gateway asks `authorize?queryRoles=true`
([role-grant authorization](../domain/role-grant-authorization.md)).

## Response — one grammar for both

```jsonc
{
  "element": { "kind": "transition", "key": "to-review" },  // kind: "transition" | "state"
  "count": 2,                                               // == attempts.length
  "attempts": [
    {
      "seq": 1,                          // 1-based, oldest first
      "startedAt": "…", "finishedAt": "…",
      "durationMs": 1104.0,
      "triggerType": "manual",
      "triggeredBy": "alice",
      "tasks": [
        {
          "id": "6f9c…",                 // journal row id (the taskId the actions function takes)
          "taskKey": "ns-mock-risk-recalc",
          "hook": "onExecute",           // onExecute | onEntry | onExit; null on pre-migration rows
          "order": 1,                    // equal order ⇒ parallel group; null on pre-migration rows
          "status": "completed",         // waiting | busy | completed | faulted
          "businessStatus": "success",   // unknown | success | failed
          "startedAt": "…",
          "durationMs": 511.0,           // null while still executing (e.g. a half "waiting" row)
          "faultedTaskRef": null,        // FaultedTaskId — always null today (no writer), shape-complete
          "error": null                  // fault reason on a faulted row; never a stack trace
        }
      ]
    }
  ]
}
```

**Every attempt is returned; filtering is the client's choice.** Retries and re-firings all appear —
the measured two-attempt `to-review` (1104 ms / 1098 ms, each with its own task set) comes back as two
attempts, not one merged view.

### `transition` vs `state` — two fields read differently, by design

The shape is identical; two attempt fields carry a different meaning per kind, and the difference is
deliberate:

| Field | transition | state |
| --- | --- | --- |
| `startedAt` / `finishedAt` | the firing's start / finish | the state's **entry** (entering transition's finish) / **exit** (leaving transition's start) |
| `durationMs` | the transition's **execution** duration (`InstanceTransitions.Duration` — not state dwell) | the visit's **dwell** (entry→exit); null while still in the state |
| `triggerType` / `triggeredBy` | that firing | the transition that **entered** the state |
| `tasks` | every task journaled under the firing — the transition's own `onExecute` **plus** the adjacent states' `onExit`/`onEntry` that ran in the same record; `hook` tells them apart | the state's `onEntry` (from the entering transition) then its `onExit` (from the leaving transition) |

The "execution, not dwell" caveat from the request applies to **transitions**, where the two are
easy to confuse. For a **state**, dwell is the answer to "how long did it sit here", so that is what
`durationMs` carries.

## How the grouping works

A transition A→B writes one `InstanceTransitions` row and runs, under that one record, A's `onExit`,
the transition's `onExecute`, and B's `onEntry` — all three journal their `InstanceTasks` rows with
that record's id as `TransitionId`. The two endpoints slice this differently:

- **transition metrics**: every record whose transition key matches is one attempt; its `tasks` are
  all rows under it (any hook).
- **state metrics**: the record that **entered** the state (`ToState == key`) supplies the visit's
  `onEntry` tasks; the next record that **left** it (`FromState == key`) supplies the `onExit` tasks.
  A record that both leaves and enters the same state (a self-loop, `FromState == ToState == key`)
  closes one visit and opens the next. A visit entered but not yet left is **half-open**: no leaving
  record, `finishedAt`/`durationMs` null, only the `onEntry` tasks — and a parallel `onEntry` sibling
  left `waiting` beside a failed one shows as a `waiting` task, surfaced not hidden.

Because `onEntry`/`onExit` are separated **by hook**, a row journaled before the hook column existed
(null hook) cannot be classified into a phase and is omitted from a state's task list. It is unknown,
not guessed — the hook is not recoverable from the one-way `ExecutionKey` hash.

## Deliberate omissions

- **Payloads.** `Request`/`Response`/`InvocationResult` are never surfaced (mapping-built auth
  material), exactly as on the tasks function; the read projects columns in SQL so the jsonb payloads
  never leave the database. `error` is the one payload-derived field — the faulted row's reason.
- **`taskType`.** The journal does not carry a task's type (HttpTask, ScriptTask, …); it is a
  definition attribute. The client already reads the definition to draw the run icons
  (`hasExecutionTasks`/`hasEntryTasks`/`hasExitTasks`), so it maps `taskKey → type` there. These
  endpoints stay a pure journal read; definition-derived fields come from the client's definition read.
- **`faultedTaskRef`.** Carried for shape stability but always null — the runtime has never had a
  writer for `InstanceTask.FaultedTaskId` (same known gap as `InstanceActions`).

## Implementation map

- Controller routes: `InstanceController.GetTransitionMetricsAsync` / `GetStateMetricsAsync`.
- Application: `InstanceQueryAppService.GetTransitionMetricsAsync` / `GetStateMetricsAsync` — resolve
  instance → read slim transition rows → group/pair → project tasks. Visit pairing is
  `PairStateVisits`.
- Repository reads: `EfCoreInstanceTransitionRepository.GetByInstanceIdAsReadOnlyAsync` (slim rows,
  no Body/Header jsonb) and `EfCoreInstanceTaskRepository.GetMetricsRowsByTransitionIdsAsync` (column
  projection into `InstanceTaskMetricsRow`, faulted Response via `CASE`).
- No state-function involvement: no `ResponseShapeVersion` or fingerprint change.
