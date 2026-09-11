# Instance Task History and Action History

Two built-in system functions over the task journal (issue #939). They complete the per-instance
history family — transitions (`…/transitions`), incidents (`…/incidents`) — with what ran inside
those transitions.

## Functions

| Function | Returns |
| --- | --- |
| `GET /{domain}/workflows/{workflow}/instances/{instance}/functions/task-history` | The instance's full task journal (`InstanceTasks`), in execution order (StartedAt ascending). |
| `GET /{domain}/workflows/{workflow}/instances/{instance}/functions/action-history?taskId={id}` | One journal row's recorded execution sub-steps (`InstanceActions`), in execution order. |

Both are `IInstanceFunctionHandler` registrations dispatched by the `{function}` route segment
(`TaskHistoryFunctionHandler` / `ActionHistoryFunctionHandler`, keys in `FunctionTypeConst`), like
`state`, `data` and `hierarchy` — so a custom function named `task-history` or `action-history` is
shadowed, the same rule every system function key has always had. `{instance}` accepts the instance
id or business key. Responses are **unpaged** — the full set returns at once; a task list is bounded
by the instance's own transition count, and actions by one task's sub-steps.

`action-history` takes the owning journal row id as the `taskId` **query parameter** (the `id` field
of a task-history item). Missing or non-GUID → `400` (`Instance:100039`).

## Authorization

Both functions are gated by the same `queryRoles` check as the state function
(`ITransitionAuthorizationManager.IsQueryAllowedAsync`, caller roles resolved through
`ICallerRoleResolver` inside the handler): a caller who may poll an instance's state may read what
ran on it. Failing the gate is `403`, so "nothing ran" stays distinguishable from "not allowed to
know".

## Task-history response

Each item projects one `InstanceTask` journal row joined with its owning transition's context:

```jsonc
{
  "items": [
    {
      "id": "6f9c…",                 // journal row id — the taskId the action-history function takes
      "taskKey": "send-otp",         // task definition key
      "transitionKey": "approve",    // owning transition + its state context
      "fromState": "draft",
      "toState": "approved",         // null while that transition is in progress
      "triggerType": "manual",
      "status": "completed",         // platform status: waiting | busy | completed | faulted
      "businessStatus": "success",   // business outcome: unknown | success | failed
      "startedAt": "…", "finishedAt": "…", "durationMs": 184.2,
      "error": null                  // fault reason, only on a faulted row; never a stack trace
    }
  ]
}
```

**Metadata only — deliberately.** The journal's `Request`, `Response` and `InvocationResult`
payloads are NOT exposed here: mapping scripts write the headers they build (including auth
material resolved from secret stores) into those columns, so full payloads are operator material.
Operators read them on the Monitor API
(`GET monitor/{domain}/workflows/{workflow}/instances/{instance}/tasks[/{taskId}]`), the same
public/Monitor split incidents use for stack traces. The one payload-derived field is `error`: a
faulted row stores its reason as `{"error": "…"}` in `Response` (`InstanceTask.Faulted`), and that
string is surfaced.

**The payloads never leave the database either.** `GetHistoryByInstanceIdAsync` projects the
metadata columns in the SQL SELECT itself (constructor projection into `InstanceTaskHistoryRow`),
so the jsonb payload columns are not read for this function at all; the Faulted rows' `Response` is
fetched through a `CASE` and is then only the small `{"error": …}` object. Do not switch this read
back to materializing the entity.

## Action-history response

`InstanceAction` rows are execution sub-steps of one journal row (`TaskId` → `InstanceTask.Id`).
The function verifies the addressed task belongs to the instance in the route
(`IInstanceTaskRepository.GetRefForInstanceAsync`) and answers `404` (`Instance:100038`) otherwise —
a task can never be read through another instance's role gate. The response echoes the owning
`taskId`/`taskKey` and lists `{ id, status, startedAt, finishedAt, durationMs, detail }` items in
execution order.

**Known gap: no writer.** Nothing in the runtime records `InstanceAction` rows today — the table
has had no production writer since the initial commit, so the function returns an empty list for
every task. The read contract is in place for when a writer lands (tracked as a follow-up to
issue #939); recording sub-steps from the task engine is its own design decision (volume, hot-path
cost) and is out of scope here.

## Implementation map

- Handlers: `TaskHistoryFunctionHandler` / `ActionHistoryFunctionHandler`
  (`orchestration/…/Controllers/Functions/Handlers/`), registered as `IInstanceFunctionHandler`.
- Repository reads: `EfCoreInstanceTaskRepository.GetHistoryByInstanceIdAsync` /
  `GetRefForInstanceAsync` (join to `InstanceTransitions` for transition context, served by
  `IX_InstanceTransitions_Instance_StartedAt`), `EfCoreInstanceActionRepository.GetByTaskIdAsync`.
- Application: `InstanceQueryAppService.GetInstanceTasksAsync` / `GetInstanceTaskActionsAsync`,
  following the incidents pattern (resolve instance → resolve flow → `queryRoles` gate → read → DTO).
- No state-function involvement: the state body carries no task links, so `ResponseShapeVersion`
  is untouched and nothing new participates in the fingerprint ETag.
- No gateway/remote plumbing and no subflow descent: like the incident history, the functions
  always answer for the addressed instance only.
