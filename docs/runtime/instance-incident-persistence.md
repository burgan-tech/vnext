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
  `TransitionChainDepthExceeded`, policy/schema failures), for
  `ExecutionErrors.UnhandledNonBlockingTaskFailures`, and for the low-probability failed-save path.
  Pinned by `TaskStepIncidentPersistenceTests`.
- **`Handle`'s `ShouldContinue` branch is unreachable from the task steps.**
  `TaskExecutionEngine.ConvertActionResult` returns a continue-style outcome as
  `TasksExecutionResult.SuccessWithFailedTasks`, which carries **no** boundary action, so the step's
  `BoundaryAction != null` guard is false and `Handle` is never called for `ignore`/`log`. The
  measured (and confirmed-correct) behaviour is therefore "no incident, and the rest of the hook is
  skipped". Left in place so the intended semantics stay expressed.
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

## Related

- [Instance Function Cache and Fingerprint ETag](state-function-cache-and-etag.md) — `HasActiveIncident` as fingerprint material, the state body's `incident` links block.
- [Workflow Execution Pipeline](../architecture/workflow-execution-pipeline.md) — task steps, `FinalizeTransitionStep`, error boundary.
