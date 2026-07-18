# Waiting Background Job Cancellation Design

**Date:** 2026-07-18

**Status:** Approved design

## Problem

vNext instance completion, fault, and cancel cleanup paths call `IBackgroundJobService.DeleteAsync` for every active `InstanceJob`. An executing job remains active until its handler finishes, so cleanup can select the job that caused the terminal transition. `DeleteAsync` then changes the Aether `BackgroundJobInfo` row from `Running` to `Cancelled` and clears its claim token.

When the handler returns, `JobDispatcher` cannot record `Completed` because its token-guarded update requires the row to remain `Running` with the original token. This produces the warning:

```text
Claim for job id '{JobId}' was lost before success could be recorded; skipping
```

The dispatcher currently writes a success log immediately afterward even though it did not persist success. OpenObserve and PostgreSQL investigation confirmed this sequence for completion and fault cleanup, including concurrent cleanup in a separate trace.

## Goals

- Completion, fault, and cancel-transition cleanup cancel only jobs that have not started.
- `Pending`, `Scheduled`, and `Retrying` jobs are cancellable.
- `Running` jobs retain their claim and finish through the dispatcher.
- The decision to cancel is atomic with respect to a concurrent `Scheduled -> Running` claim.
- vNext keeps `InstanceJob` tracking consistent with the Aether job outcome.
- `JobDispatcher` never logs successful completion when terminal success was not persisted.

## Non-Goals

- Do not add cooperative cancellation for already running handlers.
- Do not attempt to stop an in-flight Dapr callback.
- Do not change retry, visibility-timeout, or reaper semantics.
- Do not remove the existing unconditional `DeleteAsync` API; callers outside cleanup may still require its current behavior.
- Do not migrate or rewrite historical `Cancelled` rows.

## Cancellation Invariant

Platform cleanup may cancel a job only while its status is one of:

```text
Pending, Scheduled, Retrying
```

The following state is never changed by platform cleanup:

```text
Running
```

The store must enforce this invariant in one conditional database update. A read followed by `DeleteAsync` is not acceptable because the job could be claimed between those operations.

## Aether API Design

Add a cleanup-specific operation to `IBackgroundJobService`:

```csharp
Task<BackgroundJobCancellationResult> CancelWaitingAsync(
    Guid id,
    CancellationToken cancellationToken = default);
```

Add a result type with these outcomes:

```csharp
public enum BackgroundJobCancellationResult
{
    Cancelled,
    SkippedRunning,
    AlreadyTerminal,
    NotFound
}
```

`IJobStore` gains an atomic method that conditionally transitions the row:

```csharp
Task<bool> TryCancelWaitingAsync(
    Guid id,
    DateTime handledTimeUtc,
    CancellationToken cancellationToken = default);
```

The EF Core implementation performs one `ExecuteUpdateAsync` with this predicate:

```csharp
j.Id == id &&
(j.Status == BackgroundJobStatus.Pending ||
 j.Status == BackgroundJobStatus.Scheduled ||
 j.Status == BackgroundJobStatus.Retrying)
```

On success it sets:

- `Status = Cancelled`
- `HandledTime = handledTimeUtc`
- `ModifiedAt = now`
- `RunningSince = null`
- `RunningToken = null`
- `ArmingToken = null`
- `ArmingUntil = null`

If the conditional update affects no row, the service reads the current row only to classify the result:

- Missing row: `NotFound`
- `Running`: `SkippedRunning`
- Any terminal state: `AlreadyTerminal`
- A transient waiting state observed after losing another concurrent mutation is retried only once through the atomic method; no unbounded retry is introduced.

## Scheduler Side Effects

The scheduler entry is deleted only when the atomic database transition returns `Cancelled`.

- With an ambient UoW, scheduler deletion is registered through `IUnitOfWork.OnCompleted` and runs only after a successful commit.
- Without an ambient UoW, `CancelWaitingAsync` opens a transactional `RequiresNew` UoW, commits the cancellation, and then deletes the scheduler entry.
- `SkippedRunning`, `AlreadyTerminal`, and `NotFound` do not issue a scheduler deletion.

If scheduler deletion fails after commit, the DB row remains `Cancelled`. A late Dapr delivery cannot claim it because `TryClaimAsync` requires `Scheduled`. The failure remains observable through the existing scheduler error logging; durable scheduler-deletion retries are outside this change.

## vNext Cleanup Integration

`InstanceCancellationService` uses `CancelWaitingAsync` in both cleanup methods:

- `ProcessCancellationAsync`
- `ProcessStateTransitionsCancellationAsync`

The result controls `InstanceJob` processing:

| Aether result | Background job | Scheduler | InstanceJob |
|---|---|---|---|
| `Cancelled` | Set to `Cancelled` | Delete after commit | Mark processed |
| `SkippedRunning` | Unchanged | Do not delete | Leave active; handler owns completion |
| `AlreadyTerminal` | Unchanged | Do not delete | Mark processed as stale tracking |
| `NotFound` | Missing | Do not delete | Mark processed as stale tracking |

This behavior applies equally to:

- `InstanceCompletedCleanupEventHook`
- `InstanceFaultedCleanupEventHook`
- `InstanceCanceledEventHook`
- state-transition scheduled-job cleanup
- cancel transitions initiated by a user or another workflow

A cancel transition remains a business transition. Platform cleanup removes future or waiting work but does not interrupt the job currently executing that transition.

## Dispatcher Logging

`JobDispatcher.RecordSuccessAsync` returns whether the terminal result was recorded:

```csharp
private async Task<bool> RecordSuccessAsync(...)
```

`DispatchCoreAsync` writes `Successfully completed handler ...` only when this method returns `true`. When it returns `false`, the existing claim-loss warning and `job.status=claim-lost` activity tag remain, and the dispatch exits without a success log.

This change does not weaken the token guard. After waiting-job cancellation is deployed, a claim-loss warning represents a genuine concurrency, reaper, or external-state event rather than normal cleanup behavior.

## Concurrency Behavior

For a race between claiming and cleanup, exactly one operation wins:

1. If `TryClaimAsync` wins, status becomes `Running`; `TryCancelWaitingAsync` affects zero rows and returns `SkippedRunning`.
2. If `TryCancelWaitingAsync` wins, status becomes `Cancelled`; `TryClaimAsync` affects zero rows and the handler is not invoked.

No path changes a `Running` job to `Cancelled` through cleanup.

## Error Handling

- Invalid empty job IDs keep the existing argument-validation behavior.
- A DB failure propagates and follows the caller's existing cleanup error handling.
- Failure to cancel one job does not prevent `InstanceCancellationService` from attempting the remaining jobs.
- A skipped running job is an expected information-level outcome, not an error.
- The existing claim-loss warning remains a warning because it should become exceptional after this change.

## Test Strategy

### Aether store integration tests

- Each of `Pending`, `Scheduled`, and `Retrying` transitions atomically to `Cancelled`.
- `Running` is not modified and retains `RunningToken` and `RunningSince`.
- `Completed`, `Failed`, and `Cancelled` are not modified.
- A real PostgreSQL race between `TryClaimAsync` and `TryCancelWaitingAsync` has exactly one winner.

### Aether service tests

- Each result classification is returned correctly.
- Scheduler deletion occurs only for `Cancelled`.
- Ambient UoW defers scheduler deletion until commit and suppresses it on rollback.
- Non-ambient execution commits the DB state before scheduler deletion.

### Aether dispatcher tests

- Recorded success emits the success log.
- Lost success claim emits the warning but no success log.
- Existing successful, failure, retry, and recurring behaviors remain unchanged.

### vNext application tests

- Completion/fault/cancel cleanup marks cancelled waiting jobs as processed.
- Running jobs are skipped and their `InstanceJob` remains active.
- Already-terminal or missing Aether jobs cause stale `InstanceJob` rows to be marked processed.
- State-transition cleanup uses the same result rules.
- A per-job failure does not stop cleanup of later jobs.

### End-to-end regression

- Execute a transition job that completes or faults its instance.
- Verify cleanup does not change the executing Aether job to `Cancelled`.
- Verify the dispatcher records `Completed` with cleared claim fields.
- Verify other future jobs for the instance become `Cancelled`.
- Verify OpenObserve-equivalent captured logs contain neither a claim-loss warning nor a contradictory success-after-claim-loss pair.

## Rollout and Compatibility

- The new API is additive.
- Existing callers of unconditional `DeleteAsync` are unchanged.
- vNext cleanup callers switch together so completion, fault, and cancel transitions share one policy.
- No database migration is required because the design uses existing status and claim/arming columns.
- Existing `Cancelled` records remain valid historical data.

