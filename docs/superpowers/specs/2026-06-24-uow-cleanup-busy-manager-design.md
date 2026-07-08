# UoW Cleanup & Instance Busy Manager Design

**Date:** 2026-06-24  
**Branch:** feature/local-accumulated-work  
**Author:** Tayfun Yılmaz

---

## Problem Statement

Three independent issues in the instance lifecycle UoW usage:

1. **`BeginAsync` misuse** — `InstanceCommandAppService` and `InstanceRetryAppService` use the async `BeginRequiresNew(CancellationToken)` overload. Per Aether SDK spec, `BeginAsync` sets the ambient UoW inside its own async state machine; the assignment does **not** propagate back to the caller's continuations. Repository calls that follow in the same method see the HTTP middleware's outer ambient UoW instead of the intended `RequiresNew` scope.

2. **Busy-marking duplication** — The "mark instance Busy in an isolated transaction" operation is copied in three places across three different layers:
   - `Infrastructure/Execution/Locks/InstanceBusyMarker.cs` (sync pipeline pre-lock)
   - `Application/Execution/Transitions/Strategy/AsyncTransitionStrategy.SetInstanceBusyAsync` (async pre-enqueue, private method)
   - `Application/SubFlow/Services/InstanceBusyPropagationService.cs` (subflow chain propagation)
   
   `IInstanceBusyMarker` is also incorrectly placed in the Domain layer.

3. **`InstanceCancellationService` ambient UoW reliance** — The service performs job cleanup (Dapr deletes + DB marks) without owning its transaction. Its effective scope depends on which caller invokes it, making it fragile.

---

## Goals

- Fix `BeginAsync` → `Begin` in all 3 affected call sites.
- Consolidate all busy-marking logic into a single `IInstanceBusyManager` in the Application layer.
- Give `InstanceCancellationService` its own `RequiresNew` transaction.
- Remove `IInstanceBusyMarker` from Domain; remove `InstanceBusyMarker` from Infrastructure.

**Out of scope:** Broader status-management refactor (Fault, Unfault, Complete); `IsTransactional` audit across the entire codebase.

---

## Design

### 1. Fix `BeginAsync` → `Begin`

Three call sites. All follow the same correction pattern:

```csharp
// ❌ Before — BeginAsync; ambient does not propagate to caller continuations
await using var uow = await UnitOfWorkManager.BeginRequiresNew(cancellationToken);

// ✅ After — Begin (sync); ambient set immediately in caller frame
await using var uow = UnitOfWorkManager.Begin(
    new UnitOfWorkOptions
    {
        Scope = UnitOfWorkScopeOption.RequiresNew,
        IsTransactional = true   // required for TransactionLocal schema mode
    });
```

**Affected files:**

| File | Line(s) |
|------|---------|
| `Application/Instances/InstanceCommandAppService.cs` | 272 (`PrepareInstanceAsync`) |
| `Application/Instances/InstanceRetryAppService.cs` | 152 (`RetryFaultedInstanceAsync`) |
| `Application/Instances/InstanceRetryAppService.cs` | 222 (`UnfaultAndPersistAsync`) |

`IsTransactional = true` is added explicitly because `RequiresNew` UoWs opened outside the HTTP middleware do not inherit the middleware's transactional default, and `TransactionLocal` schema switching requires an open transaction.

---

### 2. `IInstanceBusyManager` — Consolidation

#### Interface

```csharp
// Application/Instances/Managers/IInstanceBusyManager.cs
namespace BBT.Workflow.Instances;

public interface IInstanceBusyManager
{
    /// <summary>
    /// Marks a single instance as Busy in an isolated RequiresNew transaction.
    /// Idempotent: no-ops silently when the instance is already Busy or Completed.
    /// Used pre-pipeline (sync path) and by the gateway for local-domain routing.
    /// </summary>
    Task MarkBusyAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an instance as Busy and propagates the Busy status down the active
    /// SubFlow chain via the instance command gateway (supports cross-domain).
    /// Used by AsyncTransitionStrategy before job enqueue.
    /// </summary>
    Task MarkBusyWithPropagationAsync(Guid instanceId, CancellationToken cancellationToken = default);
}
```

#### Implementation sketch

```csharp
// Application/Instances/Managers/InstanceBusyManager.cs
public sealed class InstanceBusyManager(
    IInstanceRepository instanceRepository,
    IUnitOfWorkManager uowManager,
    IInstanceCommandGateway instanceCommandGateway,
    ILogger<InstanceBusyManager> logger) : IInstanceBusyManager
{
    public async Task MarkBusyAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var result = await instanceRepository.GetResultAsync(
            instanceId.ToString(), includeDetails: false, cancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            logger.InstanceNotFoundForBusyMarker(instanceId);
            return;
        }

        var instance = result.Value;
        if (instance.IsBusy || instance.IsCompleted) return;

        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

        instance.Busy();
        await instanceRepository.UpdateAsync(instance, false, cancellationToken);
        await uow.CommitAsync(cancellationToken);

        logger.InstanceMarkedBusy(instanceId);
    }

    public async Task MarkBusyWithPropagationAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindWithActiveSubFlowAsync(instanceId, cancellationToken);
        if (instance is null) return;

        if (instance is { IsBusy: false, IsCompleted: false })
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            instance.Busy();
            await instanceRepository.UpdateAsync(instance, false, cancellationToken);
            await uow.CommitAsync(cancellationToken);
        }

        var subflow = instance.Subflow;
        if (subflow is not null)
        {
            await instanceCommandGateway.MarkBusyAsync(new MarkBusyInput
            {
                Domain = subflow.SubFlowDomain,
                Workflow = subflow.SubFlowName,
                InstanceId = subflow.SubFlowInstanceId,
                Version = subflow.SubFlowVersion
            }, cancellationToken);
        }
    }
}
```

#### Caller rewiring

| Caller | Old dependency | New call |
|--------|---------------|----------|
| `TransitionPipeline` | `IInstanceBusyMarker.MarkBusyAsync` | `IInstanceBusyManager.MarkBusyAsync` |
| `AsyncTransitionStrategy` | private `SetInstanceBusyAsync` | `IInstanceBusyManager.MarkBusyWithPropagationAsync` |
| `LocalInstanceCommandGateway` | `IInstanceBusyPropagationService.MarkBusyAsync` | `IInstanceBusyManager.MarkBusyWithPropagationAsync` |

#### Files removed

| File | Reason |
|------|--------|
| `Domain/Execution/Transitions/Pipeline/IInstanceBusyMarker.cs` | Replaced by `IInstanceBusyManager` |
| `Infrastructure/Execution/Locks/InstanceBusyMarker.cs` | Logic absorbed into `InstanceBusyManager` |
| `Application/SubFlow/Services/InstanceBusyPropagationService.cs` | Logic absorbed into `InstanceBusyManager` |
| `Application/SubFlow/Contracts/IInstanceBusyPropagationService.cs` | Interface removed |
| `AsyncTransitionStrategy.SetInstanceBusyAsync` (private method) | Deleted; delegated to manager |

---

### 3. `InstanceCancellationService` — Own Its UoW

The service currently relies on the caller's ambient UoW. Both public methods must open their own `RequiresNew` transaction.

`IUnitOfWorkManager` is added to the constructor. `autoSave: true` on each `UpdateAsync` is changed to `false`; a single `CommitAsync` at the end flushes all changes in one round-trip.

Dapr `backgroundJobService.DeleteAsync` calls remain inside the `foreach` loop wrapped in per-job `try/catch` — they are idempotent and fast. The DB mark (`UpdateAsync`) follows each Dapr call within the same loop iteration; per-job failure handling is preserved.

```csharp
// ProcessCancellationAsync — new shape
await using var uow = uowManager.Begin(
    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

foreach (var job in jobs)
{
    try
    {
        await backgroundJobService.DeleteAsync(job.JobId, cancellationToken);
        job.MarkAsProcessed();
        await instanceJobRepository.UpdateAsync(job, false, cancellationToken); // autoSave: false
    }
    catch (Exception ex) { logger.InstanceJobDeletionFailed(ex, job.JobId, instanceId); }
}

await uow.CommitAsync(cancellationToken);
```

Same pattern applies to `ProcessStateTransitionsCancellationAsync`.

---

### 4. DI Registration Changes

```csharp
// Removed
services.AddScoped<IInstanceBusyMarker, InstanceBusyMarker>();
services.AddScoped<IInstanceBusyPropagationService, InstanceBusyPropagationService>();

// Added
services.AddScoped<IInstanceBusyManager, InstanceBusyManager>();

// IInstanceCancellationService registration unchanged; ctor gains IUnitOfWorkManager
```

---

## File Change Summary

| File | Change |
|------|--------|
| `Application/Instances/Managers/IInstanceBusyManager.cs` | **New** |
| `Application/Instances/Managers/InstanceBusyManager.cs` | **New** |
| `Application/Instances/InstanceCommandAppService.cs` | Fix `BeginAsync` L272 |
| `Application/Instances/InstanceRetryAppService.cs` | Fix `BeginAsync` L152, L222 |
| `Application/Instances/Managers/InstanceCancellationService.cs` | Add UoW; `autoSave: false` |
| `Application/Execution/Transitions/Strategy/AsyncTransitionStrategy.cs` | Remove `SetInstanceBusyAsync`; inject + call `IInstanceBusyManager` |
| `Application/Execution/Transitions/Pipeline/TransitionPipeline.cs` | `IInstanceBusyMarker` → `IInstanceBusyManager` |
| `Infrastructure/Gateway/LocalInstanceCommandGateway.cs` | `IInstanceBusyPropagationService` → `IInstanceBusyManager` |
| `Application/Microsoft/.../WorkflowApplicationModuleServiceCollectionExtensions.cs` | DI rewire |
| `HttpApi.Shared/Microsoft/.../WorkflowApiBaseServiceCollectionExtensions.cs` | DI rewire |
| `Domain/Execution/Transitions/Pipeline/IInstanceBusyMarker.cs` | **Deleted** |
| `Infrastructure/Execution/Locks/InstanceBusyMarker.cs` | **Deleted** |
| `Application/SubFlow/Services/InstanceBusyPropagationService.cs` | **Deleted** |
| `Application/SubFlow/Contracts/IInstanceBusyPropagationService.cs` | **Deleted** |

---

## Risks & Mitigations

| Risk | Mitigation |
|------|-----------|
| `IsTransactional = true` added where it was implicitly false — behavior change | All 3 sites are in HTTP request context where a transaction is already expected; this makes it explicit |
| `MarkBusyWithPropagationAsync` does two DB round-trips (`FindWithActiveSubFlowAsync` + `UpdateAsync`) vs previous single-load in `AsyncTransitionStrategy` | Acceptable: load is lightweight (`includeDetails: false` equivalent), happens before lock-guarded enqueue |
| `autoSave: false` in `InstanceCancellationService` — uncommitted rows if exception thrown mid-loop | Per-job `try/catch` catches Dapr failures; DB mark only fails if EF throws, which rolls back on dispose |

---

## Logging

`InstanceBusyManager` uses structured log extensions from `WorkflowLogs.cs` (never raw `logger.Log*`). Two new `[LoggerMessage]` entries required:

| Method | Level | EventId range | Message |
|--------|-------|---------------|---------|
| `InstanceNotFoundForBusyMarker` | Warning | 20xxx | `"Instance {InstanceId} not found for busy marker"` |
| `InstanceMarkedBusy` | Debug | 20xxx | `"Instance {InstanceId} marked Busy (isolated UoW)"` |

---

## Non-Goals

- Refactoring `Fault`, `Unfault`, `Complete` operations into managers (separate initiative).
- Consolidating `SubflowFaultService` UoW patterns (already correct, `Begin` sync used throughout).
- Changing pipeline step internal UoW semantics (`SetBusyStep` correctly uses ambient pipeline UoW).
