# UoW Cleanup & Instance Busy Manager Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix three `BeginAsync` misuses, consolidate busy-marking into `IInstanceBusyManager`, and give `InstanceCancellationService` its own transaction.

**Architecture:** `IInstanceBusyManager` is a new Application-layer service in `Instances/Managers/` that absorbs `IInstanceBusyMarker` (Domain+Infrastructure), `IInstanceBusyPropagationService` (Application/SubFlow), and the inline `SetInstanceBusyAsync` private method in `AsyncTransitionStrategy`. All `RequiresNew` UoW openings use the synchronous `Begin()` overload with `IsTransactional = true`.

**Tech Stack:** C# 10+, Aether SDK (`IUnitOfWorkManager`, `UnitOfWorkOptions`, `UnitOfWorkScopeOption`), xUnit, NSubstitute, Shouldly.

---

## File Map

| Action | Path |
|--------|------|
| **New** | `src/BBT.Workflow.Application/Instances/Managers/IInstanceBusyManager.cs` |
| **New** | `src/BBT.Workflow.Application/Instances/Managers/InstanceBusyManager.cs` |
| **New** | `test/BBT.Workflow.Application.Tests/Instances/Managers/InstanceBusyManagerTests.cs` |
| **Modify** | `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs` |
| **Modify** | `src/BBT.Workflow.Application/Instances/InstanceCommandAppService.cs` |
| **Modify** | `src/BBT.Workflow.Application/Instances/InstanceRetryAppService.cs` |
| **Modify** | `src/BBT.Workflow.Application/Instances/Managers/InstanceCancellationService.cs` |
| **Modify** | `src/BBT.Workflow.Application/Execution/Transitions/Strategy/AsyncTransitionStrategy.cs` |
| **Modify** | `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionPipeline.cs` |
| **Modify** | `src/BBT.Workflow.Infrastructure/Gateway/LocalInstanceCommandGateway.cs` |
| **Modify** | `src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/WorkflowApplicationModuleServiceCollectionExtensions.cs` |
| **Modify** | `src/BBT.Workflow.HttpApi.Shared/Microsoft/Extensions/DependencyInjection/WorkflowApiBaseServiceCollectionExtensions.cs` |
| **Delete** | `src/BBT.Workflow.Domain/Execution/Transitions/Pipeline/IInstanceBusyMarker.cs` |
| **Delete** | `src/BBT.Workflow.Infrastructure/Execution/Locks/InstanceBusyMarker.cs` |
| **Delete** | `src/BBT.Workflow.Application/SubFlow/Services/InstanceBusyPropagationService.cs` |
| **Delete** | `src/BBT.Workflow.Application/SubFlow/Contracts/IInstanceBusyPropagationService.cs` |

---

## Task 1: Fix `BeginAsync` → `Begin` in AppService methods

**Files:**
- Modify: `src/BBT.Workflow.Application/Instances/InstanceCommandAppService.cs:272`
- Modify: `src/BBT.Workflow.Application/Instances/InstanceRetryAppService.cs:152`
- Modify: `src/BBT.Workflow.Application/Instances/InstanceRetryAppService.cs:222`

No new tests — the existing suite covers these paths. The change is mechanical.

- [ ] **Step 1: Fix `InstanceCommandAppService.PrepareInstanceAsync` (L272)**

In `PrepareInstanceAsync`, replace:
```csharp
await using var uow = await UnitOfWorkManager.BeginRequiresNew(cancellationToken);
```
With:
```csharp
await using var uow = UnitOfWorkManager.Begin(
    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
```

- [ ] **Step 2: Fix `InstanceRetryAppService.RetryFaultedInstanceAsync` (L152)**

Replace:
```csharp
await using (var uow = await UnitOfWorkManager.BeginRequiresNew(cancellationToken))
{
    instance.Unfault();
    await instanceRepository.UpdateAsync(instance, true, cancellationToken);
    await uow.CommitAsync(cancellationToken);
}
```
With:
```csharp
await using var uow = UnitOfWorkManager.Begin(
    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
instance.Unfault();
await instanceRepository.UpdateAsync(instance, true, cancellationToken);
await uow.CommitAsync(cancellationToken);
```

- [ ] **Step 3: Fix `InstanceRetryAppService.UnfaultAndPersistAsync` (L222)**

Replace:
```csharp
await using var uow = await UnitOfWorkManager.BeginRequiresNew(cancellationToken);
```
With:
```csharp
await using var uow = UnitOfWorkManager.Begin(
    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
```

- [ ] **Step 4: Build to confirm no compile errors**

```bash
dotnet build src/BBT.Workflow.Application/BBT.Workflow.Application.csproj --no-restore -v q
```
Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Application/Instances/InstanceCommandAppService.cs \
        src/BBT.Workflow.Application/Instances/InstanceRetryAppService.cs
git commit -m "fix(uow): replace BeginAsync with Begin sync in AppService UoW scopes"
```

---

## Task 2: Add `WorkflowLogs.cs` entries for `InstanceBusyManager`

**Files:**
- Modify: `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs`

The current highest 10xxx EventId in this file is **10127**. Use 10128 and 10129.

- [ ] **Step 1: Add two `[LoggerMessage]` partials to `WorkflowLogs.cs`**

Find the block of instance-related log methods (search for `10082` or `10127` to locate the end of the instance section) and append:

```csharp
[LoggerMessage(
    EventId = 10128,
    Level = LogLevel.Warning,
    Message = "Instance {InstanceId} not found for busy marker — skipping")]
public static partial void InstanceNotFoundForBusyMarker(this ILogger logger, Guid instanceId);

[LoggerMessage(
    EventId = 10129,
    Level = LogLevel.Debug,
    Message = "Instance {InstanceId} marked Busy via isolated UoW")]
public static partial void InstanceMarkedBusy(this ILogger logger, Guid instanceId);
```

- [ ] **Step 2: Build Domain project to confirm generated partials compile**

```bash
dotnet build src/BBT.Workflow.Domain/BBT.Workflow.Domain.csproj --no-restore -v q
```
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs
git commit -m "feat(logging): add InstanceNotFoundForBusyMarker and InstanceMarkedBusy log extensions"
```

---

## Task 3: Create `IInstanceBusyManager`

**Files:**
- Create: `src/BBT.Workflow.Application/Instances/Managers/IInstanceBusyManager.cs`

- [ ] **Step 1: Create the interface file**

```csharp
namespace BBT.Workflow.Instances;

/// <summary>
/// Manages the Busy status of workflow instances with isolated transactions.
/// Consolidates pre-pipeline busy marking, async pre-enqueue marking, and SubFlow chain propagation.
/// </summary>
public interface IInstanceBusyManager
{
    /// <summary>
    /// Marks a single instance as Busy in an isolated RequiresNew transaction.
    /// Idempotent: silently no-ops when the instance is already Busy, Completed, or not found.
    /// </summary>
    Task MarkBusyAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an instance as Busy and propagates down the active SubFlow chain via the
    /// instance command gateway (cross-domain capable).
    /// Idempotent: silently no-ops when the instance is already Busy or Completed.
    /// </summary>
    Task MarkBusyWithPropagationAsync(Guid instanceId, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Build to confirm interface compiles**

```bash
dotnet build src/BBT.Workflow.Application/BBT.Workflow.Application.csproj --no-restore -v q
```
Expected: 0 errors.

---

## Task 4: Create `InstanceBusyManager` + unit tests

**Files:**
- Create: `src/BBT.Workflow.Application/Instances/Managers/InstanceBusyManager.cs`
- Create: `test/BBT.Workflow.Application.Tests/Instances/Managers/InstanceBusyManagerTests.cs`

- [ ] **Step 1: Write the failing tests first**

Create `test/BBT.Workflow.Application.Tests/Instances/Managers/InstanceBusyManagerTests.cs`:

```csharp
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Instances.Managers;

public class InstanceBusyManagerTests
{
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IUnitOfWorkManager _uowManager = Substitute.For<IUnitOfWorkManager>();
    private readonly IInstanceCommandGateway _gateway = Substitute.For<IInstanceCommandGateway>();
    private readonly ILogger<InstanceBusyManager> _logger = Substitute.For<ILogger<InstanceBusyManager>>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly InstanceBusyManager _sut;

    public InstanceBusyManagerTests()
    {
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(_uow);
        _sut = new InstanceBusyManager(_instanceRepository, _uowManager, _gateway, _logger);
    }

    // ──────────────────────────────────────────────────────────
    // MarkBusyAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task MarkBusyAsync_WhenInstanceNotFound_ShouldSkipWithoutThrow()
    {
        var instanceId = Guid.NewGuid();
        _instanceRepository
            .GetResultAsync(instanceId.ToString(), false, Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Fail(Error.NotFound("INSTANCE_NOT_FOUND", "not found", instanceId.ToString())));

        await _sut.MarkBusyAsync(instanceId); // should not throw

        _uowManager.DidNotReceive().Begin(Arg.Any<UnitOfWorkOptions>());
    }

    [Fact]
    public async Task MarkBusyAsync_WhenInstanceAlreadyBusy_ShouldSkipUoW()
    {
        var instanceId = Guid.NewGuid();
        var instance = CreateInstance(instanceId);
        instance.Busy();

        _instanceRepository
            .GetResultAsync(instanceId.ToString(), false, Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Ok(instance));

        await _sut.MarkBusyAsync(instanceId);

        _uowManager.DidNotReceive().Begin(Arg.Any<UnitOfWorkOptions>());
    }

    [Fact]
    public async Task MarkBusyAsync_WhenInstanceActive_ShouldMarkBusyAndCommit()
    {
        var instanceId = Guid.NewGuid();
        var instance = CreateInstance(instanceId);

        _instanceRepository
            .GetResultAsync(instanceId.ToString(), false, Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Ok(instance));

        await _sut.MarkBusyAsync(instanceId);

        _uowManager.Received(1).Begin(Arg.Is<UnitOfWorkOptions>(o =>
            o.Scope == UnitOfWorkScopeOption.RequiresNew && o.IsTransactional));
        await _instanceRepository.Received(1).UpdateAsync(instance, false, Arg.Any<CancellationToken>());
        await _uow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────
    // MarkBusyWithPropagationAsync
    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task MarkBusyWithPropagationAsync_WhenNoSubflow_ShouldMarkAndNotCallGateway()
    {
        var instanceId = Guid.NewGuid();
        var instance = CreateInstance(instanceId); // no active subflow

        _instanceRepository
            .FindWithActiveSubFlowAsync(instanceId, Arg.Any<CancellationToken>())
            .Returns(instance);

        await _sut.MarkBusyWithPropagationAsync(instanceId);

        _uowManager.Received(1).Begin(Arg.Any<UnitOfWorkOptions>());
        await _gateway.DidNotReceive().MarkBusyAsync(Arg.Any<MarkBusyInput>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkBusyWithPropagationAsync_WhenSubflowActive_ShouldPropagateToGateway()
    {
        var instanceId = Guid.NewGuid();
        var subflowId = Guid.NewGuid();
        var instance = CreateInstanceWithSubflow(instanceId, subflowId);

        _instanceRepository
            .FindWithActiveSubFlowAsync(instanceId, Arg.Any<CancellationToken>())
            .Returns(instance);

        await _sut.MarkBusyWithPropagationAsync(instanceId);

        await _gateway.Received(1).MarkBusyAsync(
            Arg.Is<MarkBusyInput>(i => i.InstanceId == subflowId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkBusyWithPropagationAsync_WhenInstanceNull_ShouldNoOp()
    {
        var instanceId = Guid.NewGuid();
        _instanceRepository
            .FindWithActiveSubFlowAsync(instanceId, Arg.Any<CancellationToken>())
            .Returns((Instance?)null);

        await _sut.MarkBusyWithPropagationAsync(instanceId); // should not throw

        _uowManager.DidNotReceive().Begin(Arg.Any<UnitOfWorkOptions>());
    }

    // ──────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────

    private static Instance CreateInstance(Guid instanceId) =>
        Instance.Create(instanceId, "test-flow", "1.0.0", null);

    private static Instance CreateInstanceWithSubflow(Guid instanceId, Guid subflowId)
    {
        var instance = Instance.Create(instanceId, "test-flow", "1.0.0", null);
        // Add an active SubFlow correlation so instance.Subflow is non-null.
        // Use the domain method that creates a subflow correlation; adjust to match your domain API.
        instance.AddSubFlowCorrelation(subflowId, "sub-flow", "1.0.0", "test-domain", SubFlowType.SubFlow, "parent-state");
        return instance;
    }
}
```

- [ ] **Step 2: Run tests to verify they fail (class not found)**

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~InstanceBusyManagerTests" --no-build 2>&1 | tail -5
```
Expected: Build error — `InstanceBusyManager` not defined.

- [ ] **Step 3: Implement `InstanceBusyManager`**

Create `src/BBT.Workflow.Application/Instances/Managers/InstanceBusyManager.cs`:

```csharp
using BBT.Aether.Uow;
using BBT.Workflow.Gateway;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances;

/// <inheritdoc cref="IInstanceBusyManager" />
public sealed class InstanceBusyManager(
    IInstanceRepository instanceRepository,
    IUnitOfWorkManager uowManager,
    IInstanceCommandGateway instanceCommandGateway,
    ILogger<InstanceBusyManager> logger) : IInstanceBusyManager
{
    /// <inheritdoc />
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
        if (instance.IsBusy || instance.IsCompleted)
            return;

        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

        instance.Busy();
        await instanceRepository.UpdateAsync(instance, false, cancellationToken);
        await uow.CommitAsync(cancellationToken);

        logger.InstanceMarkedBusy(instanceId);
    }

    /// <inheritdoc />
    public async Task MarkBusyWithPropagationAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        var instance = await instanceRepository.FindWithActiveSubFlowAsync(instanceId, cancellationToken);
        if (instance is null)
            return;

        if (instance is { IsBusy: false, IsCompleted: false })
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            instance.Busy();
            await instanceRepository.UpdateAsync(instance, false, cancellationToken);
            await uow.CommitAsync(cancellationToken);

            logger.InstanceMarkedBusy(instanceId);
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

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~InstanceBusyManagerTests" -v n
```
Expected: all tests PASS. If `CreateInstanceWithSubflow` fails because `AddSubFlowCorrelation` doesn't match the actual domain API, check `Instance`'s aggregate methods and adjust the helper accordingly.

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Application/Instances/Managers/IInstanceBusyManager.cs \
        src/BBT.Workflow.Application/Instances/Managers/InstanceBusyManager.cs \
        test/BBT.Workflow.Application.Tests/Instances/Managers/InstanceBusyManagerTests.cs
git commit -m "feat(instances): add IInstanceBusyManager consolidating all busy-marking operations"
```

---

## Task 5: Update `TransitionPipeline` — swap `IInstanceBusyMarker` → `IInstanceBusyManager`

**Files:**
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionPipeline.cs`

- [ ] **Step 1: Replace the dependency in the constructor**

In `TransitionPipeline`, locate:
```csharp
IInstanceBusyMarker busyMarker,
```
Replace with:
```csharp
IInstanceBusyManager busyMarker,
```

Also update the field declaration from:
```csharp
private readonly IInstanceBusyMarker _busyMarker;
```
To:
```csharp
private readonly IInstanceBusyManager _busyMarker;
```

The call site `_busyMarker.MarkBusyAsync(instanceId, cancellationToken)` does not change — both interfaces share this method name with the same signature.

- [ ] **Step 2: Update the `using` directive if needed**

`IInstanceBusyMarker` was in `BBT.Workflow.Execution.Pipeline` (Domain). `IInstanceBusyManager` is in `BBT.Workflow.Instances` (Application). Remove the old using and add the new one if the namespace differs from what's already imported.

- [ ] **Step 3: Build**

```bash
dotnet build src/BBT.Workflow.Application/BBT.Workflow.Application.csproj --no-restore -v q
```
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/BBT.Workflow.Application/Execution/Transitions/Pipeline/TransitionPipeline.cs
git commit -m "refactor(pipeline): replace IInstanceBusyMarker with IInstanceBusyManager"
```

---

## Task 6: Update `AsyncTransitionStrategy` — remove private method, inject manager

**Files:**
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Strategy/AsyncTransitionStrategy.cs`

- [ ] **Step 1: Inject `IInstanceBusyManager` into the constructor**

Add `IInstanceBusyManager instanceBusyManager,` to the constructor parameter list and store it as a field:
```csharp
private readonly IInstanceBusyManager _instanceBusyManager = instanceBusyManager;
```

- [ ] **Step 2: Replace the `SetInstanceBusyAsync` call site**

Find the line in the public `ExecuteAsync` method that calls:
```csharp
await SetInstanceBusyAsync(ctx, cancellationToken);
```
Replace it with:
```csharp
if (!ctx.Directives.IsInternalResume)
    await _instanceBusyManager.MarkBusyWithPropagationAsync(ctx.Instance.Id, cancellationToken);
```

> **`ctx.Instance.Id` not `ctx.InstanceId`:** `TransitionExecutionContext.InstanceId` is a `string` (inherited from `WorkflowExecutionContext`), but `MarkBusyWithPropagationAsync` takes a `Guid`. Use `ctx.Instance.Id` (the domain entity's Guid) to avoid a `Guid.Parse` call.

> **Why keep the `IsInternalResume` guard here?** SubFlow resume paths manage Busy via `ClearBusyOnResumeStep` — the manager must not re-mark busy on resume. This is caller-specific logic that does not belong inside the manager.

- [ ] **Step 3: Delete the private `SetInstanceBusyAsync` method entirely**

Remove the entire private method body:
```csharp
private async Task SetInstanceBusyAsync(
    TransitionExecutionContext ctx,
    CancellationToken cancellationToken)
{
    // ... entire method ...
}
```

- [ ] **Step 4: Remove now-unused `IInstanceRepository` and `IUnitOfWorkManager` imports if they are only used by the deleted method**

Check if `instanceRepository` and `uowManager` are still used elsewhere in `AsyncTransitionStrategy`. If not, remove them from the constructor and fields.

- [ ] **Step 5: Build**

```bash
dotnet build src/BBT.Workflow.Application/BBT.Workflow.Application.csproj --no-restore -v q
```
Expected: 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/BBT.Workflow.Application/Execution/Transitions/Strategy/AsyncTransitionStrategy.cs
git commit -m "refactor(async-strategy): delegate busy marking to IInstanceBusyManager"
```

---

## Task 7: Update `LocalInstanceCommandGateway` — swap propagation service

**Files:**
- Modify: `src/BBT.Workflow.Infrastructure/Gateway/LocalInstanceCommandGateway.cs`

- [ ] **Step 1: Replace `IInstanceBusyPropagationService` with `IInstanceBusyManager`**

Find the `MarkBusyAsync` method body (around L153):
```csharp
var busyService = sp.GetRequiredService<IInstanceBusyPropagationService>();
await busyService.MarkBusyAsync(input, ct);
```
Replace with:
```csharp
var busyManager = sp.GetRequiredService<IInstanceBusyManager>();
await busyManager.MarkBusyWithPropagationAsync(input.InstanceId, ct);
```

- [ ] **Step 2: Remove the old `using` for `IInstanceBusyPropagationService` namespace if it becomes unused**

The namespace was `BBT.Workflow.SubFlow` (or similar). Remove it if no other type from that namespace is referenced in this file.

- [ ] **Step 3: Build**

```bash
dotnet build src/BBT.Workflow.Infrastructure/BBT.Workflow.Infrastructure.csproj --no-restore -v q
```
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/BBT.Workflow.Infrastructure/Gateway/LocalInstanceCommandGateway.cs
git commit -m "refactor(gateway): replace IInstanceBusyPropagationService with IInstanceBusyManager"
```

---

## Task 8: Update `InstanceCancellationService` — own its UoW

**Files:**
- Modify: `src/BBT.Workflow.Application/Instances/Managers/InstanceCancellationService.cs`

- [ ] **Step 1: Add `IUnitOfWorkManager` to the constructor**

Change the constructor primary parameters to include `IUnitOfWorkManager uowManager`:
```csharp
public sealed class InstanceCancellationService(
    IInstanceRepository instanceRepository,
    IInstanceJobRepository instanceJobRepository,
    IBackgroundJobService backgroundJobService,
    IUnitOfWorkManager uowManager,
    ILogger<InstanceCancellationService> logger)
    : IInstanceCancellationService
```

- [ ] **Step 2: Update `ProcessCancellationAsync`**

Wrap the existing logic in a `RequiresNew` UoW and change all `autoSave: true` to `autoSave: false`:
```csharp
public async Task<Result> ProcessCancellationAsync(
    Guid instanceId,
    CancellationToken cancellationToken = default)
{
    try
    {
        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

        var instance = await instanceRepository.FindAsync(instanceId, true, cancellationToken);
        if (instance == null)
        {
            logger.InstanceNotFound(instanceId, string.Empty);
            return Result.Fail(WorkflowErrors.InstanceNotFound(instanceId.ToString()));
        }

        var jobs = await instanceJobRepository.GetListActiveAsync(instance.Id, cancellationToken);
        if (!jobs.Any())
        {
            await uow.CommitAsync(cancellationToken);
            return Result.Ok();
        }

        foreach (var job in jobs)
        {
            try
            {
                await backgroundJobService.DeleteAsync(job.JobId, cancellationToken);
                job.MarkAsProcessed();
                await instanceJobRepository.UpdateAsync(job, false, cancellationToken); // autoSave: false
            }
            catch (Exception ex)
            {
                logger.InstanceJobDeletionFailed(ex, job.JobId, instanceId);
            }
        }

        logger.InstanceCanceledJobsProcessed(instanceId, jobs.Count);
        await uow.CommitAsync(cancellationToken);
        return Result.Ok();
    }
    catch (Exception ex)
    {
        logger.InstanceCanceledProcessingFailed(ex, instanceId);
        return Result.Fail(WorkflowErrors.InstanceCancellationFailed(instanceId, ex.Message));
    }
}
```

- [ ] **Step 3: Apply the same pattern to `ProcessStateTransitionsCancellationAsync`**

Wrap that method's body in a `RequiresNew` UoW in the same way. Open the UoW at the start, change all `autoSave: true` to `autoSave: false`, call `uow.CommitAsync(cancellationToken)` before each `return Result.Ok()`.

- [ ] **Step 4: Add `using BBT.Aether.Uow;` if not already present**

- [ ] **Step 5: Build**

```bash
dotnet build src/BBT.Workflow.Application/BBT.Workflow.Application.csproj --no-restore -v q
```
Expected: 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/BBT.Workflow.Application/Instances/Managers/InstanceCancellationService.cs
git commit -m "fix(cancellation): give InstanceCancellationService its own RequiresNew transaction"
```

---

## Task 9: DI Rewire

**Files:**
- Modify: `src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/WorkflowApplicationModuleServiceCollectionExtensions.cs`
- Modify: `src/BBT.Workflow.HttpApi.Shared/Microsoft/Extensions/DependencyInjection/WorkflowApiBaseServiceCollectionExtensions.cs`

- [ ] **Step 1: Update `WorkflowApplicationModuleServiceCollectionExtensions.cs`**

Remove (line 75):
```csharp
services.AddScoped<IInstanceBusyPropagationService, InstanceBusyPropagationService>();
```
Add in its place (or anywhere in the instance manager group near line 81):
```csharp
services.AddScoped<IInstanceBusyManager, InstanceBusyManager>();
```

- [ ] **Step 2: Update `WorkflowApiBaseServiceCollectionExtensions.cs`**

Remove (lines 237-238):
```csharp
services.AddScoped<BBT.Workflow.Execution.Pipeline.IInstanceBusyMarker,
    BBT.Workflow.Infrastructure.Execution.Locks.InstanceBusyMarker>();
```
`IInstanceBusyManager` is already registered in the Application DI extension above — no duplicate needed here.

- [ ] **Step 3: Build the full solution**

```bash
dotnet build --no-restore -v q
```
Expected: 0 errors, 0 warnings related to unresolved types.

- [ ] **Step 4: Commit**

```bash
git add src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/WorkflowApplicationModuleServiceCollectionExtensions.cs \
        src/BBT.Workflow.HttpApi.Shared/Microsoft/Extensions/DependencyInjection/WorkflowApiBaseServiceCollectionExtensions.cs
git commit -m "chore(di): register IInstanceBusyManager, remove obsolete busy-marker registrations"
```

---

## Task 10: Delete removed files

**Files to delete:**
- `src/BBT.Workflow.Domain/Execution/Transitions/Pipeline/IInstanceBusyMarker.cs`
- `src/BBT.Workflow.Infrastructure/Execution/Locks/InstanceBusyMarker.cs`
- `src/BBT.Workflow.Application/SubFlow/Services/InstanceBusyPropagationService.cs`
- `src/BBT.Workflow.Application/SubFlow/Contracts/IInstanceBusyPropagationService.cs`

- [ ] **Step 1: Delete the four files**

```bash
rm src/BBT.Workflow.Domain/Execution/Transitions/Pipeline/IInstanceBusyMarker.cs
rm src/BBT.Workflow.Infrastructure/Execution/Locks/InstanceBusyMarker.cs
rm src/BBT.Workflow.Application/SubFlow/Services/InstanceBusyPropagationService.cs
rm src/BBT.Workflow.Application/SubFlow/Contracts/IInstanceBusyPropagationService.cs
```

- [ ] **Step 2: Build full solution to confirm no dangling references**

```bash
dotnet build --no-restore -v q
```
Expected: 0 errors. If any file still imports `IInstanceBusyMarker` or `IInstanceBusyPropagationService`, fix the reference before proceeding.

- [ ] **Step 3: Run all tests**

```bash
dotnet test --no-build -v n 2>&1 | tail -20
```
Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git add -u  # stages deletions
git commit -m "chore: remove IInstanceBusyMarker, InstanceBusyMarker, IInstanceBusyPropagationService, InstanceBusyPropagationService"
```

---

## Task 11: Final Verification

- [ ] **Step 1: Full solution build**

```bash
dotnet build --no-restore
```
Expected: 0 errors, 0 warnings for this change set.

- [ ] **Step 2: Run all tests**

```bash
dotnet test
```
Expected: all tests pass; no regressions.

- [ ] **Step 3: Confirm deleted files are gone**

```bash
find src -name 'IInstanceBusyMarker.cs' -o -name 'InstanceBusyMarker.cs' \
         -o -name 'IInstanceBusyPropagationService.cs' -o -name 'InstanceBusyPropagationService.cs'
```
Expected: no output.

- [ ] **Step 4: Confirm `BeginAsync` is gone from the fixed files**

```bash
grep -n 'await.*BeginRequiresNew\|BeginAsync' \
  src/BBT.Workflow.Application/Instances/InstanceCommandAppService.cs \
  src/BBT.Workflow.Application/Instances/InstanceRetryAppService.cs
```
Expected: no matches.

---

## Commit History (expected)

```
fix(uow): replace BeginAsync with Begin sync in AppService UoW scopes
feat(logging): add InstanceNotFoundForBusyMarker and InstanceMarkedBusy log extensions
feat(instances): add IInstanceBusyManager consolidating all busy-marking operations
refactor(pipeline): replace IInstanceBusyMarker with IInstanceBusyManager
refactor(async-strategy): delegate busy marking to IInstanceBusyManager
refactor(gateway): replace IInstanceBusyPropagationService with IInstanceBusyManager
fix(cancellation): give InstanceCancellationService its own RequiresNew transaction
chore(di): register IInstanceBusyManager, remove obsolete busy-marker registrations
chore: remove IInstanceBusyMarker, InstanceBusyMarker, IInstanceBusyPropagationService, InstanceBusyPropagationService
```
