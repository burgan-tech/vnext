# History Mode (`attributes.history`)

## Purpose

A flow that runs very often and is never looked at afterwards (a lookup, an introspection, a quote)
paid the same storage cost as a long-lived business process: an `InstancesData` row per append, an
`InstanceTransitions` row per transition and `InstanceTasks` journal rows, with no retention.
`attributes.history: "none"` (vnext#1006) marks such a flow as **one-shot**: it stays a state machine
(states, transitions, correlation, a queryable instance), but writes no transition or task history
and a single data row.

```json
{
  "key": "payments-quote",
  "attributes": {
    "type": "F",
    "history": "none",
    "states": []
  }
}
```

`history` is `none` or `full`; omitted means `full`. It sits at the root of `attributes` (beside
`type` and `executionType`), never under `config`. Rollback is removing the field — no migration.

## Boundaries

- Only the flow's own definition is validated at publish. SubFlow children are checked when they start.
- The instance row, incidents, jobs and correlations are written as for any flow.
- `history: none` is a property of the definition: each instance follows its own flow's mode.

## Publish rules

When `history` is `none` the flow must start, run only automatic transitions and always finish.
`WorkflowValidator.ValidateHistoryNone` rejects (as **errors**, never warnings):

| Rule | Also in vnext-schema |
|------|----------------------|
| At least one Finish state | yes |
| Every state transition is Automatic (`triggerType: 1`) | yes |
| Every non-Finish state, SubFlow states included, has at least one transition (several automatic transitions are fine; a `triggerKind: 10` default is not required) | yes |
| No `sharedTransitions`, `cancel`, `exit`, `updateData` | yes |
| No workflow `timeout`, no `subFlow.overrides.timeout` | yes |
| No `interaction.longPoll`, no Wizard state, no Suspended / Busy / Human subtype | yes |
| Every state can reach a Finish state (no cycle, no `$self`-only loop, no dead end) | runtime only |

`startTransition` stays manual, and a workflow-level `event` start, SubFlow states and SubProcess
tasks are allowed. An unknown `history` value fails deserialization and is a publish error. An older
runtime or schema ignores the field silently (attributes are open, unknown members are dropped) — ship
the schema and runtime together.

## SubFlow rule

A `none` parent requires a `none` SubFlow (`S`) child. `SubflowStarter` stamps
`parent.history = none` on the child's start metadata (only for `none` parents); the child's
`InstanceCommandAppService.StartAsync` checks it right after loading its definition, before anything
is persisted, and fails with `Instance:100044` (400) when its own definition is not `none`. The parent
then faults through the existing post-commit coordination path, with an incident. The stamp travels in
`ExtraProperties`, so the rule holds across domains; the public start endpoint does not accept it.
SubProcess (`P`) children are exempt, and a `none` child under a `full` parent is allowed.

## Persistence

**`InstanceTransitions` / `InstanceTasks` are never written.** `CreateTransitionRecordStep` keeps the
record in memory (scripts still read `CurrentTransition`; marked fresh so no task-journal probe runs)
and only saves the instance's own fields; `FinalizeTransitionStep` skips the record update (incidents
are still resolved); `TaskExecutionEngine` uses no persistence strategy.

**`InstancesData` is buffered.** `TransitionContextFactory` attaches an `InstanceDataBuffer` to the
aggregate; `InstanceDataWriteService.AppendAsync` then merges into the buffer's pending row with the
usual `PlanAppend` rules and schema validation, without touching the database. The pending row is the
aggregate's `LatestData` but never enters the EF `DataList` navigation, and every snapshot (script
context, parallel branches) shares the same buffer, so parallel task outputs merge under the
per-instance write gate. The start path writes no initial row; the start transition maps the payload
again in the pipeline.

`FlushAsync` writes the **accumulated delta** onto the persisted head as one row (encryption, hashing
and validation as for a direct append; the version folds every accepted strategy onto that head).
Flush points:

| Point | Where | Notes |
|-------|-------|-------|
| Finish | `HandleFinishStep` | Completion is saved inside the flush transaction — Completed never commits without its data |
| SubFlow handoff | `HandleSubFlowStep` | Written with the correlation, before `StartSubflowJob`: the child's input mapping, the output mapping and the resume read the parent from the database |
| In-pipeline fault | `TransitionPipeline.MarkInstanceFaultedAsync` | Best effort, in its own unit of work; a failed write is logged (`InstanceDataBufferFlushFailed`) and the fault proceeds |

Row count: one per instance without SubFlow; with N SubFlow states visited, at most `1 + 2·N` (the
handoff write plus the SubFlow output-mapping append, which runs on a reloaded parent and stays a
direct append). Duplicate content still dedups.

**A stage that rests at a non-Finish state is a fault.** When a `none` stage comes to rest without
completing — for example every automatic rule evaluated false — the instance is faulted with incident
`Instance:100046` (`HistoryNoneNotTerminal`) and its buffer written. A rest with an open SubFlow (the
handoff) or under a non-owner only writes the buffer. Chain-depth, continuation and next-hop validation
failures, which otherwise return the error without faulting, also fault a `none` instance.

## HTTP surface

| Endpoint | Behaviour for a `none` instance |
|----------|---------------------------------|
| `POST …/instances/{instance}/retry` | `409` `Instance:100045`, before any branch runs |
| `GET …/instances/{instance}/transitions` | `200` with an empty list |
| `GET …/functions/tasks` | `200` with an empty list |
| `GET …/functions/actions?taskId=` | `200` with an empty list (not `404`) |
| transition / state metrics | empty |

While a `none` instance runs it has no data row: GET instance / data / state answer with null data,
and attribute filters (inner join on `InstancesData`) do not find it.

## Failure modes (accepted)

- Post-commit faults, job timeouts and process crashes run in a fresh scope after the in-memory data
  is gone: the instance faults with an incident but without the data since the last flush.
- Retry is not available (`409`): the flow writes neither the open transition record nor the task
  journal retry relies on.
- `$PreviousUser` / `$PreviousBehalfOfUser` grants never resolve (no transition history).
- `x-encryption` fields are sealed at flush; during the run scripts see newly written values in
  plaintext, and `DecryptAsync` returns null for them.

## Observability

Logs: `SubFlowChildHistoryNotSuppressed` (20475), `InstanceRetryRejectedHistoryNone` (20476),
`InstanceDataBuffered` (20477, Debug), `InstanceDataBufferFlushed` (20478),
`InstanceDataBufferDrift` (20479, the persisted head moved under the buffer),
`InstanceDataBufferFlushFailed` (20480), `HistoryNoneStageFaulted` (20481). The flush is an ordinary
`Instance.AppendData` span.

## Change safety

- Keep the buffer outside the EF navigation: a tracked `UpdateAsync` would insert it early, a detached
  `Attach` would mark it Unchanged.
- A flush on a DbContext other than the stage's (the fault path's own unit of work) must go through a
  snapshot of the aggregate: the write accepts the persisted row into the aggregate it is given, and on
  the stage's tracked aggregate the stage commit inserts it again (`23505 PK_InstancesData` — measured
  on the first integration run, 2026-10-07).
- Never pass a callback to `FlushAsync` that appends data — the per-instance gate is not re-entrant.
- The flush relies on the merge being associative (`InstanceDataWriteServiceBufferTests`).

## References

- `src/BBT.Workflow.Domain/Definitions/HistoryMode.cs`, `Workflow.SuppressesHistory`
- `src/BBT.Workflow.Domain/Definitions/Validators/WorkflowValidator.cs` (`ValidateHistoryNone`)
- `src/BBT.Workflow.Domain/Instances/InstanceDataBuffer.cs`
- `src/BBT.Workflow.Infrastructure/Data/InstanceDataWriteService.cs` (`AppendBufferedAsync`, `FlushAsync`)
- `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionPipeline.cs` (`SettleHistoryNoneStageAsync`)
- [Instance Data Merge Concept](../domain/instance-data-merge-concept.md),
  [Instance Task and Action History](instance-task-and-action-history.md)
