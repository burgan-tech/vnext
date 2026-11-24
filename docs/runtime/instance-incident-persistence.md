# Instance Incident Persistence and Retry

This page holds the persistence rules for `InstanceIncident` rows and the retry path that resolves
them: how incidents are (not) loaded with the aggregate, where a task step records one, why resolve
is set-based, and why retry loads no-tracking and unfaults with a CAS. Each rule below was paid for by
a measured defect; the quick-reference card in `.claude/rules/vnext-workflow-developer.md` keeps only
the one-line imperatives.

## Load-time includes (context)

- Pipeline steps do NOT call EF `Include` directly. Includes applied at load time.
- `EfCoreInstanceRepository.WithDetailsAsync()` loads `DataList` (or latest-only when
  `WorkflowExecution:LatestOnlyInstanceLoading` is on) + `Include(ChildCorrelations.Where(!IsCompleted))`
  (split queries).
- `GetActiveAsync` → `GetResultAsync` → `FindByIdentifierAsync` → `WithDetailsAsync()`.
- `GetResultAsync(includeDetails: false)` is lean (no DataList/correlations).
- History paths: `AsNoTracking` + explicit filtered includes.
- Post-commit settlement (`FindForPostCommitSettlementAsync(id, includeLatestData)`): open correlations
  **always** (settlement guard + fault cascade read `ActiveCorrelations`); the latest data row only when
  something downstream projects it — sync caller (`TransitionOutput.PipelineInstance` →
  `EnrichOutputCoreAsync`) or a fault (`Instance.Fault` publishes a SubFlow's data upward). Async settle
  skips it. The decision matrix lives on `PostCommitParentMutationService.NeedsLatestDataForSettle`;
  add a new `LatestData` reader there, not by re-widening the include.

## Incident rows

- **Incidents are never included.** `InstanceIncident` rows live in `InstanceIncidents` (unbounded
  history, cascade with the instance) and `Instance.HasActiveIncident` is a denormalized column the
  aggregate maintains. Guards read the flag; anything that needs the unresolved incidents (resolve on
  retry/`FinalizeTransitionStep`, `Fault`'s upward payload, script `context.Incident`, the state body)
  calls `IInstanceRepository.LoadActiveIncidentsAsync` first — no query when the flag is false.
  `ResolveOpenIncidents` throws if the flag is set and nothing was loaded. New rows are inserted by
  `EfCoreInstanceRepository.UpdateAsync` from `GetPendingIncidents()` (marked `Added` before Aether's
  detached `Set.Update(graph)` would stamp them `Modified`). History/paging/batch reads go through
  `IInstanceIncidentRepository`, not the aggregate.
- **Rows read back are NOT put on the EF navigation.** `LoadActiveIncidentsAsync`'s no-tracking branch
  hands them to `Instance.AcceptLoadedIncidents`, which keeps them in a detached list that EF cannot
  see; `GetLoadedIncidents()` merges both sources for readers. Attaching them to the navigation made
  every *other* context tracking that aggregate discover them as new children and re-INSERT them —
  the retry path loads the instance in the ambient request scope and its incidents inside a
  `RequiresNew` one, so its commit died with `23505 PK_InstanceIncidents` and a half-written response
  body. Consequence: resolving a **detached** incident writes nothing by itself
  (`Instance.IsDetachedIncident` says which), so `InstanceRetryAppService` persists it through
  `IInstanceIncidentRepository.ResolveAllAsync`, unconditionally — the retry path now loads
  no-tracking, so there is never a graph to save. Pipeline-created incidents are tracked and still
  save through the graph. Pinned by `InstanceIncidentPersistenceTests`.
- **A task step records the incident BEFORE its own save; nothing records one after a save.** The
  three task steps (`RunOn{Execute,Entry,Exit}TasksStep`) call `BoundaryOutcomeHandler` and *then*
  `UpdateAsync(instance, autoSave: true)`, so the row and the `HasActiveIncident` flag commit
  together. Both step UoWs are non-transactional, which is what makes that save immediately visible.
  It has to be, because an abort returns `Fail` and `TransitionPipeline.MarkInstanceFaultedAsync`
  reloads the aggregate in its OWN `RequiresNew` UoW; it skips its fallback incident only when the
  **committed** flag already says one exists. Recording after the save produced two rows for one
  failure — the boundary's verdict plus a bare `ErrorBoundaryAbort` pipeline row that was the newer
  of the two, so `incident.active` on a faulted instance carried no boundary verdict at all. The
  unhandled path deliberately does NOT call `ApplyScriptContextChanges` (its only payload is a
  script's `Stage` mutation, which a faulting run should not persist) and wraps the save in
  try/catch so the step still fails with the original task error. The fallback branch stays: it is
  the only recorder for pipeline errors with no task error (`ResourceLockConflict`,
  `TransitionChainDepthExceeded`, policy/schema failures) and for the low-probability failed-save path.
  A task that fails with **no boundary at any level** takes the unhandled path above: the engine
  returns it with its `TaskError` and no `BoundaryAction`, so the step records the incident and the
  instance faults (vnext#1257; before, it came back error-less and the step read it as success).
  Pinned by `TaskStepIncidentPersistenceTests`.
- **`Handle`'s `ShouldContinue` branch is unreachable from the task steps.**
  `TaskExecutionEngine.ConvertActionResult` returns a continue-style outcome as
  `TasksExecutionResult.HandledFailure`, which carries **no** boundary action, so the step's
  `BoundaryAction != null` guard is false and `Handle` is never called for `ignore`/`log`. The
  behaviour is therefore "no incident; `TaskCoordinator` runs the remaining tasks of the hook and
  the hook completes" (vnext#1257 — before, the rest of the hook was skipped). Left in place so the
  intended semantics stay expressed.
- **Resolve is set-based.** `Instance.ResolveOpenIncidents()` closes EVERY unresolved row on the
  materialized aggregate and recomputes the flag from that same snapshot; there is deliberately no
  single-row API to pick wrongly. One failure can leave more than one open row (a job-timeout
  recovery on top of a boundary incident, a boundary transition that faults on its own, a parent
  taking a subflow fault while it already carries one), and resolving only the newest left a
  recovered instance reporting a stale active incident — visible to long-pollers because
  `HasActiveIncident` is fingerprint material. `FinalizeTransitionStep` relies on the pipeline
  aggregate being tracked and needs no repository call; the retry path is detached and always calls
  `ResolveAllAsync`.
- **Retry loads no-tracking and unfaults with a CAS.** `InstanceRetryAppService` reads through
  `IInstanceRepository.GetResultAsReadOnlyAsync` and flips the status with `TryUnfaultAsync`
  (one `ExecuteUpdateAsync` guarded on `Status == Faulted`, plus baseline alignment). Never load an
  aggregate tracked in the ambient request UoW, mutate it, and leave the write to an inner
  `RequiresNew` scope: the ambient commit at the end of the request wrote its own stale `Active`
  over the `Faulted` the inner scope had persisted, leaving an instance that looked healthy, had not
  finished its work, and could never be retried again (`Instance:100027`). The unfault must also
  **commit before** `ExecuteRetryAsync`, because the pipeline's `GetActiveAsync` rejects a faulted
  instance. Pinned by
  `InstanceIncidentPersistenceTests.TryUnfaultAsync_FlipsFaultedToActiveAndLeavesNothingForAnAmbientCommitToOverwrite`.

## Stack traces

`InstanceIncidents."StackTrace"` holds the exception behind the failure, rendered with
`Exception.ToString()`: type, message, then the inner exceptions, innermost first, each with its
frames. It is capped at 4096 characters (`InstanceIncidentConstants.MaxStackTraceLength`; the head —
type, message, innermost frames — is what survives). No API serves it (`IncidentDetailDto` has no
such field). It travels between runtimes in one place: `InstanceSubFaultedEvent.IncidentStackTrace`
copies a faulted child's trace into its parent's incident, across domains too. It reaches the
incident like this (vnext#1268):

| Failure | Where the exception is kept | Incident |
|---|---|---|
| A task script throws — an input/output mapping of any task type, the script task's own handler, a FanOut selector/mapping, a cache key | `ScriptContext.FailureCause` (internal), set by `ScriptDiagnostics.TaskScriptError`; `TaskExecutionEngine` takes it after every executor run and copies it into `ExecutionError.StackTrace` | the task step's (`BoundaryOutcomeHandler`), boundary or not |
| Any other exception the task engine catches (an executor or invoker that throws) | `ExecutionErrorFactory.CreateFromException` | the task step's |
| A transition mapping, a resource-lock key script or a timer script throws; an exception escapes a pipeline step (agent-state scripts included) or automatic-rule evaluation | `TransitionExecutionContext.FailureCause` | the pipeline's fault incident (`MarkInstanceFaultedAsync`) |
| A SubFlow input or output mapping throws | `Error.Detail` of `SubFlowInputMappingFailed` / `SubFlowOutputMappingFailed` (pre-existing: their detail is the post-commit incident channel) | the parent's (`PostCommitParentMutationService`, `SubflowCompletionService`) |
| An automatic-transition rule script throws | not kept: the rule counts as not satisfied and nothing is recorded — unchanged; the log carries it | none |

- **An `Error` never carries it,** because Aether writes an error's `Detail` into the HTTP error body.
  The SubFlow mapping rows are the pre-existing exception; their detail can still reach a client on one
  path (`POST …/retry` restarting a missing child returns `SubFlowInputMappingFailed` as it is).
- **Codes and messages do not change.** An unhandled task failure used to reach the task step as a
  failed `Result` that `TaskCoordinator` re-derived into an `ExecutionError`, which dropped the trace.
  The coordinator now asks the engine for an outcome instead
  (`TaskEngineExecutionOptions.ReportUnhandledFailureAsOutcome`), and the engine re-derives the error
  the same way the coordinator did, keeping only the trace in addition. FanOut items and agent tools
  keep the failed `Result`; their cancellation handling reads it.
- **A cause never outlives its run.** The engine takes the script context's cause after every
  executor run — in a `finally`, so a run that throws or succeeds leaves nothing — and
  `CreateParallelBranch` does not copy it, so it cannot attach to another task's failure.
- **A cancellation a script sees is not its failure.** An `OperationCanceledException` thrown inside a
  task script, a transition mapping, a lock-key, timer or rule script is reported exactly as before but
  kept nowhere and not logged as a script failure. An exception that escapes a pipeline step is the
  hop's cause whatever it is, a cancellation included.
- **Script line numbers.** A script's frames carry the line in the author's own source; how, and
  what it costs: [Script Context and Engine § Observability](script-context-and-engine.md#observability).
- **The same detail in the log.** `TaskScriptFailed` (10268) logs a task script's exception once, in
  the engine; `InstanceFaultedDueToPipelineError` (10130) carries the hop's cause;
  `StartTransitionMappingFailed` (10269) logs a start mapping that fails — the start answers with the
  error and records no incident; `ConditionScriptFailed` (10270) logs a rule script that throws, which records no incident.

## Related

- [Instance Function Cache and Fingerprint ETag](state-function-cache-and-etag.md) — `HasActiveIncident` as fingerprint material, the state body's `incident` links block.
- [Script Context and Engine](script-context-and-engine.md) — the script compiler, line numbers in script frames.
- [Workflow Execution Pipeline](../architecture/workflow-execution-pipeline.md) — task steps, `FinalizeTransitionStep`, error boundary.
