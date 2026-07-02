# SubFlow Resume Lock Isolation & Correlation Revert Fix — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop parent instances from getting permanently stuck in `Busy` when `sync=true` chains contain multiple blocking subflows, by (a) making the subflow-resume lock key unique per completing sub-instance and (b) fixing the silent no-op in correlation revert caused by the filtered `ChildCorrelations` include.

**Architecture:** Two independent fixes that compose. Fix A threads the completing `subInstanceId` from `SubflowCompletionService`/`SubflowFaultService` through `ExecutionInfo` → `TransitionContextFactory` → `PipelineDirectives` so `ReservedTransitionResolver` can build `{lockKey}:resume:{subInstanceId:N}` — a nested sync resume (triggered inside an outer resume chain's post-commit `StartSubflowJob`) no longer collides with the outer chain's `:resume` lock. Fix B adds `IInstanceRepository.FindWithAllCorrelationsAsync` (no `!IsCompleted` filter) and uses it in both revert paths, so a correlation completed in Phase 1 can actually be reverted after a Phase 2 (resume) failure; retries then find the correlation again instead of reporting "Correlation not found" and leaving the parent Busy forever.

**Tech Stack:** .NET 10, EF Core (Aether `EfCoreRepository`), xUnit + Moq + Shouldly. Source-generated logging via `WorkflowLogs.cs` (never raw `logger.Log*`).

**Root cause recap (from log analysis of 2026-07-02, `transaction-limit-verify` instances `59cc4bb5`, `250268bc`):**
1. Sync chain: parent start → subflow #1 completes sync → resume chain acquires `{lockKey}:resume` and holds it for its whole chain, including post-commit jobs (`TransitionPipeline.RunChainAsync`, line ~167) → chain enters a second subflow state → `StartSubflowJob` runs subflow #2 synchronously inside that lock scope → subflow #2's completion tries to acquire the same `{lockKey}:resume` → `TryAcquireLockAsync` fails immediately (no wait) → `SubflowCompletionException`.
2. The revert then reloads the parent via `FindAsync(id, true)` whose `WithDetailsAsync` filters `ChildCorrelations.Where(c => !c.IsCompleted)` — the just-completed correlation is not loaded, `RevertCorrelation` returns `null`, nothing is persisted, no error is raised. Every retry (inner-bus fallback handler, `/complete` callback) then hits `SubFlowCorrelationNotFound` and returns "cleanly". Parent stays `Busy` forever.

**Behavioral invariants (do not break):**
- Duplicate deliveries of the *same* subflow completion (hook + Inbox handler + `/complete` callback) must still be mutually excluded → they share the same `:resume:{subInstanceId}` key.
- `MarkAsSubFlowResume()` without an id must keep producing the legacy `:resume` key (rolling-deploy safety; ~10 existing test call sites stay source-compatible via a default parameter).
- `sync=false` behavior is unchanged — the new key only removes a self-collision that never occurs there.

---

### Task 1: Per-sub resume lock key — directives, execution info, resolver

**Files:**
- Modify: `src/BBT.Workflow.Domain/Execution/Transitions/Context/PipelineDirectives.cs` (property near `IsSubFlowResume` ~line 53, method `MarkAsSubFlowResume` ~line 133)
- Modify: `src/BBT.Workflow.Domain/Execution/Transitions/Context/ExecutionInfo.cs`
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Factory/TransitionContextFactory.cs:138-139`
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/ReservedTransitionResolver.cs:30`
- Test: `test/BBT.Workflow.Application.Tests/Execution/Transitions/Pipeline/ReservedTransitionResolverTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `ReservedTransitionResolverTests` (next to `GetOwnLockKey_WithSubFlowResume_ShouldUseResumeLabel`):

```csharp
    [Fact]
    public void GetOwnLockKey_WithSubFlowResumeAndSubInstanceId_ShouldIncludeSubInstanceId()
    {
        var subInstanceId = Guid.NewGuid();
        var ctx = CreateContext(transitionKey: "resume");
        ctx.Directives.MarkAsSubFlowResume(subInstanceId);
        _resolver.GetOwnLockKey(ctx).ShouldBe($"{ctx.LockKey}:resume:{subInstanceId:N}");
    }

    [Fact]
    public void GetOwnLockKey_WithSubFlowResumeWithoutSubInstanceId_ShouldFallBackToLegacyResumeKey()
    {
        var ctx = CreateContext(transitionKey: "resume");
        ctx.Directives.MarkAsSubFlowResume();
        _resolver.GetOwnLockKey(ctx).ShouldBe(ctx.LockKey + ":resume");
    }

    [Fact]
    public void GetOwnLockKey_TwoDifferentSubInstances_ShouldProduceDifferentKeys()
    {
        var ctx1 = CreateContext(transitionKey: "resume");
        var ctx2 = CreateContext(transitionKey: "resume");
        ctx1.Directives.MarkAsSubFlowResume(Guid.NewGuid());
        ctx2.Directives.MarkAsSubFlowResume(Guid.NewGuid());
        _resolver.GetOwnLockKey(ctx1).ShouldNotBe(_resolver.GetOwnLockKey(ctx2));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~ReservedTransitionResolverTests"`
Expected: the first and third new tests FAIL (compile error: no overload of `MarkAsSubFlowResume` taking a Guid). Fix compilation by doing Step 3, then re-run to see assertion behavior.

- [ ] **Step 3: Implement**

`PipelineDirectives.cs` — replace the existing `IsSubFlowResume` property block and `MarkAsSubFlowResume` method:

```csharp
    /// <summary>
    /// Gets a value indicating whether this execution is resuming from a subflow.
    /// </summary>
    public bool IsSubFlowResume { get; private set; }

    /// <summary>
    /// Gets the completing SubFlow instance id for a subflow-resume execution.
    /// Used to build a per-sub-instance resume lock key so a nested sync resume
    /// (triggered inside an outer resume chain's post-commit) does not collide
    /// with the outer chain's resume lock. Null falls back to the legacy shared key.
    /// </summary>
    public Guid? SubFlowResumeInstanceId { get; private set; }
```

```csharp
    /// <summary>
    /// Marks this execution as a subflow resume scenario.
    /// </summary>
    /// <param name="subInstanceId">The completing SubFlow instance id; scopes the resume lock per sub-instance.</param>
    public void MarkAsSubFlowResume(Guid? subInstanceId = null)
    {
        IsSubFlowResume = true;
        SubFlowResumeInstanceId = subInstanceId;
    }
```

`ExecutionInfo.cs` — add after `IsSubFlowResume`:

```csharp
    /// <summary>Gets or sets the completing SubFlow instance id when this execution
    /// is resuming from a SubFlow completion. Scopes the resume lock per sub-instance.</summary>
    public Guid? SubFlowResumeInstanceId { get; set; }
```

`TransitionContextFactory.cs:138-139` — replace:

```csharp
        if (input.Execution?.IsSubFlowResume == true)
            executionContext.Directives.MarkAsSubFlowResume(input.Execution.SubFlowResumeInstanceId);
```

`ReservedTransitionResolver.cs:30` — replace the resume line:

```csharp
        if (context.Directives.IsSubFlowResume)
        {
            // Per-sub-instance key: a nested sync resume must not collide with the outer
            // resume chain that is still holding the shared key for this parent.
            return context.Directives.SubFlowResumeInstanceId is { } subId
                ? $"{context.LockKey}:resume:{subId:N}"
                : context.LockKey + ":resume";
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~ReservedTransitionResolverTests"`
Expected: ALL PASS (including the pre-existing `GetOwnLockKey_WithSubFlowResume_ShouldUseResumeLabel`, which now exercises the legacy fallback).

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Domain/Execution/Transitions/Context/PipelineDirectives.cs \
        src/BBT.Workflow.Domain/Execution/Transitions/Context/ExecutionInfo.cs \
        src/BBT.Workflow.Application/Execution/Transitions/Factory/TransitionContextFactory.cs \
        src/BBT.Workflow.Application/Execution/Transitions/Pipeline/ReservedTransitionResolver.cs \
        test/BBT.Workflow.Application.Tests/Execution/Transitions/Pipeline/ReservedTransitionResolverTests.cs
git commit -m "feat(pipeline): scope subflow-resume lock key per completing sub-instance"
```

---

### Task 2: Producers pass the completing subInstanceId

**Files:**
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubflowCompletionService.cs:233-239` (`ResumePipelineAsync` → `ExecutionInfo`)
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubflowFaultService.cs:300-303` (`ResumePipelineAsync` → sets `IsSubFlowResume`)
- Test: `test/BBT.Workflow.Application.Tests/SubFlow/SubflowCompletionServiceTests.cs`
- Test: `test/BBT.Workflow.Application.Tests/SubFlow/SubflowFaultServiceTests.cs`

- [ ] **Step 1: Write the failing test (completion service)**

Add to `SubflowCompletionServiceTests` (uses the file's existing `CreateParentInstance`/`CreateInput`/`CreateService` helpers and the mock setups shown in `CompletionAsync_ResumesParentPipelineWithCallerModeFromInput`):

```csharp
    [Fact]
    public async Task CompletionAsync_ResumesParentPipelineWithSubFlowResumeInstanceId()
    {
        var parentInstance = CreateParentInstance(out var subInstanceId);
        var input = CreateInput(parentInstance.Id, subInstanceId);
        var parentWorkflow = WorkflowFactory.CreateDefault("parent-flow", "bank", "1.0.0");

        _instanceRepository
            .Setup(x => x.FindAsync(parentInstance.Id, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parentInstance);
        _instanceRepository
            .Setup(x => x.UpdateAsync(parentInstance, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parentInstance);
        _componentCacheStore
            .Setup(x => x.GetFlowAsync("bank", "parent-flow", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Ok(parentWorkflow));
        _outputMappingService
            .Setup(x => x.ApplyAsync(parentInstance, parentWorkflow, It.IsAny<string>(),
                It.IsAny<JsonElement?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());

        WorkflowExecutionContext? captured = null;
        _workflowExecutionService
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowExecutionContext, CancellationToken>((ctx, _) => captured = ctx)
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Id = parentInstance.Id,
                Status = InstanceStatus.Active
            }));

        await CreateService().CompletionAsync(input, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.Execution!.IsSubFlowResume.ShouldBeTrue();
        captured.Execution.SubFlowResumeInstanceId.ShouldBe(subInstanceId);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~SubflowCompletionServiceTests.CompletionAsync_ResumesParentPipelineWithSubFlowResumeInstanceId"`
Expected: FAIL — `SubFlowResumeInstanceId` is null.

- [ ] **Step 3: Implement (both services)**

`SubflowCompletionService.ResumePipelineAsync`, in the `ExecutionInfo` initializer (lines 233-239), add the id:

```csharp
                Execution = new ExecutionInfo
                {
                    ExecutionChainId = Guid.NewGuid().ToString("N"),
                    ChainDepth = 0,
                    ResumeFrom = LifecycleOrder.ClearBusyOnResumeStep,
                    IsSubFlowResume = true,
                    SubFlowResumeInstanceId = subInstanceId
                }
```

`SubflowFaultService.ResumePipelineAsync` (~line 300-303), after `input.Execution.IsSubFlowResume = true;` add:

```csharp
            input.Execution.SubFlowResumeInstanceId = subInstanceId;
```

- [ ] **Step 4: Write the analogous fault-service test and run both**

Add to `SubflowFaultServiceTests` a test that mirrors its existing resume-asserting test (reuse that file's `CreateParentInstance`, `CreateParentWorkflow`, `SetupParent`, `CreateInput`, `CreateService` helpers): capture the `WorkflowExecutionContext` passed to `_workflowExecutionService.ExecuteTransitionAsync` via the same `Callback` pattern as Step 1, then assert:

```csharp
        captured.ShouldNotBeNull();
        captured.Execution!.IsSubFlowResume.ShouldBeTrue();
        captured.Execution.SubFlowResumeInstanceId.ShouldBe(subInstanceId);
```

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~SubflowCompletionServiceTests|FullyQualifiedName~SubflowFaultServiceTests"`
Expected: ALL PASS.

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Application/SubFlow/Services/SubflowCompletionService.cs \
        src/BBT.Workflow.Application/SubFlow/Services/SubflowFaultService.cs \
        test/BBT.Workflow.Application.Tests/SubFlow/SubflowCompletionServiceTests.cs \
        test/BBT.Workflow.Application.Tests/SubFlow/SubflowFaultServiceTests.cs
git commit -m "feat(subflow): pass completing sub-instance id to resume lock scope"
```

---

### Task 3: Repository — load correlations without the completed-filter

**Files:**
- Modify: `src/BBT.Workflow.Domain/Instances/IInstanceRepository.cs` (add near `FindWithActiveSubFlowAsync`, ~line 113)
- Modify: `src/BBT.Workflow.Infrastructure/Instances/EfCoreInstanceRepository.cs` (add next to `FindWithActiveSubFlowAsync`, ~line 44)
- New log: `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs` (place next to `SubFlowCorrelationReverted`, EventId 40023, ~line 848)

- [ ] **Step 1: Add the interface method**

`IInstanceRepository.cs`:

```csharp
    /// <summary>
    /// Finds an instance including ALL child correlations (completed and active) as a tracked entity.
    /// Required by correlation revert: the default detail load filters out completed correlations,
    /// which would make reverting a just-completed correlation a silent no-op.
    /// </summary>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The instance with all correlations, or null when not found.</returns>
    Task<Instance?> FindWithAllCorrelationsAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default);
```

- [ ] **Step 2: Implement in EfCoreInstanceRepository**

Add directly below `FindWithActiveSubFlowAsync`:

```csharp
    /// <inheritdoc />
    public async Task<Instance?> FindWithAllCorrelationsAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var dbSet = await GetDbSetAsync();
        return await dbSet
            .Include(i => i.ChildCorrelations)
            .FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);
    }
```

- [ ] **Step 3: Add the missed-revert log message**

`WorkflowLogs.cs`, directly after the `SubFlowCorrelationReverted` partial (EventId 40023). Verified next free id in the 40xxx event series: **40119**.

```csharp
    /// <summary>
    /// Logs when a correlation revert finds no matching completed correlation —
    /// the parent may be permanently stuck Busy and requires manual intervention.
    /// </summary>
    [LoggerMessage(
        EventId = 40119,
        Level = LogLevel.Error,
        Message = "SubFlow correlation revert found no completed correlation for SubInstance {SubInstanceId}, Parent {ParentInstanceId} — parent may be stuck Busy")]
    public static partial void SubFlowCorrelationRevertTargetMissing(
        this ILogger logger,
        Guid subInstanceId,
        Guid parentInstanceId);
```

- [ ] **Step 4: Build**

Run: `dotnet build src/BBT.Workflow.Infrastructure`
Expected: build succeeds (source-generated logger partial compiles, repository implements the new interface member).

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Domain/Instances/IInstanceRepository.cs \
        src/BBT.Workflow.Infrastructure/Instances/EfCoreInstanceRepository.cs \
        src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs
git commit -m "feat(instances): add FindWithAllCorrelationsAsync for correlation revert"
```

---

### Task 4: Fix the silent revert no-op in SubflowCompletionService

**Files:**
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubflowCompletionService.cs:338-357` (`RevertAndPersistCorrelationAsync`)
- Test: `test/BBT.Workflow.Application.Tests/SubFlow/SubflowCompletionServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Simulates the production incident: Phase 1 committed (correlation completed), resume fails, and the fresh reload (as in production) does NOT contain the completed correlation. The revert must go through the unfiltered load and persist the reverted correlation.

```csharp
    [Fact]
    public async Task CompletionAsync_WhenResumeFails_RevertsCorrelationViaUnfilteredLoad()
    {
        var parentInstance = CreateParentInstance(out var subInstanceId);
        var input = CreateInput(parentInstance.Id, subInstanceId);
        var parentWorkflow = WorkflowFactory.CreateDefault("parent-flow", "bank", "1.0.0");

        // Phase-1 load returns the tracked parent (with active correlation).
        _instanceRepository
            .Setup(x => x.FindAsync(parentInstance.Id, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(parentInstance);
        _instanceRepository
            .Setup(x => x.UpdateAsync(It.IsAny<Instance>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Instance i, bool _, CancellationToken _) => i);
        _componentCacheStore
            .Setup(x => x.GetFlowAsync("bank", "parent-flow", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Ok(parentWorkflow));
        _outputMappingService
            .Setup(x => x.ApplyAsync(parentInstance, parentWorkflow, It.IsAny<string>(),
                It.IsAny<JsonElement?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result.Ok());

        // Resume fails hard (e.g. lock conflict) → revert path must run.
        _workflowExecutionService
            .Setup(x => x.ExecuteTransitionAsync(It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Fail(
                Error.Conflict(WorkflowErrorCodes.InstanceLockConflict, "lock", "conflict")));

        // The revert reload returns a fresh entity WITH the completed correlation
        // (what FindWithAllCorrelationsAsync guarantees and FindAsync does not).
        var reloaded = CloneParentWithCompletedCorrelation(parentInstance, subInstanceId);
        _instanceRepository
            .Setup(x => x.FindWithAllCorrelationsAsync(parentInstance.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reloaded);

        await Should.ThrowAsync<SubflowCompletionException>(
            () => CreateService().CompletionAsync(input, CancellationToken.None));

        // Revert went through the unfiltered load and was persisted.
        _instanceRepository.Verify(
            x => x.FindWithAllCorrelationsAsync(parentInstance.Id, It.IsAny<CancellationToken>()),
            Times.Once);
        reloaded.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeFalse();
        _instanceRepository.Verify(
            x => x.UpdateAsync(reloaded, true, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static Instance CloneParentWithCompletedCorrelation(Instance source, Guid subInstanceId)
    {
        var clone = Instance.Create(Guid.NewGuid(), "parent-flow", "1.0.0", "parent-key");
        typeof(Instance).GetProperty(nameof(Instance.Id))!.SetValue(clone, source.Id);
        clone.ChangeState(StateFactory.CreateDefault("waiting-child", StateType.SubFlow));
        clone.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(), clone.Id, "waiting-child", subInstanceId,
            SubFlowType.SubFlow.Code, "bank", "child-flow", "1.0.0"));
        clone.CompleteCorrelation(subInstanceId);
        return clone;
    }
```

Note: if `Instance.Id` has no public setter, keep the reflection line as shown (it targets the backing property); if that fails at runtime, fall back to asserting on `It.Is<Instance>(i => i.FindCorrelationBySubInstanceId(subInstanceId) != null && !i.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted)` in the `UpdateAsync` verify and drop the clone-id assignment — the behavioral assertion (reverted + persisted) is what matters.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~SubflowCompletionServiceTests.CompletionAsync_WhenResumeFails_RevertsCorrelationViaUnfilteredLoad"`
Expected: FAIL — `FindWithAllCorrelationsAsync` never called (production code still uses `FindAsync`).

- [ ] **Step 3: Implement**

Replace `RevertAndPersistCorrelationAsync` in `SubflowCompletionService.cs` (lines 338-357):

```csharp
    /// <summary>
    /// Reverts the SubFlow correlation and persists the changes to the repository.
    /// </summary>
    private async Task RevertAndPersistCorrelationAsync(
        Instance parentInstance,
        Guid subInstanceId,
        Guid parentInstanceId,
        CancellationToken cancellationToken)
    {
        // S9 isolation rule: do NOT mutate the detached Phase-1 entity inside this new UoW —
        // reload by id so we operate on an entity tracked by the current scope's DbContext.
        // MUST load with ALL correlations: the default detail load filters completed
        // correlations out, which silently skipped the revert and left the parent stuck Busy.
        var tracked = await instanceRepository.FindWithAllCorrelationsAsync(parentInstanceId, cancellationToken)
                      ?? parentInstance;

        var correlation = tracked.RevertCorrelation(subInstanceId);
        if (correlation == null)
        {
            logger.SubFlowCorrelationRevertTargetMissing(subInstanceId, parentInstanceId);
            return;
        }

        logger.SubFlowCorrelationReverted(subInstanceId, parentInstanceId);

        await instanceRepository.UpdateAsync(tracked, true, cancellationToken);
    }
```

- [ ] **Step 4: Run the full completion-service test class**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~SubflowCompletionServiceTests"`
Expected: ALL PASS (existing tests unaffected — they never reach the revert path or already mock `FindAsync`).

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Application/SubFlow/Services/SubflowCompletionService.cs \
        test/BBT.Workflow.Application.Tests/SubFlow/SubflowCompletionServiceTests.cs
git commit -m "fix(subflow): revert correlation via unfiltered load so retry can resume parent"
```

---

### Task 5: Align SubflowFaultService revert with the fixed pattern

**Files:**
- Modify: `src/BBT.Workflow.Application/SubFlow/Services/SubflowFaultService.cs:355-378` (`RevertCorrelationInNewUowAsync`)
- Test: `test/BBT.Workflow.Application.Tests/SubFlow/SubflowFaultServiceTests.cs`

`SubflowFaultService` has the mirror-image bug variant: it reverts on the **detached** Phase-1 entity (works in memory, but violates the S9 isolation rule — change-tracker bleed, and `UpdateAsync` on a detached aggregate in a `RequiresNew` UoW is unreliable).

- [ ] **Step 1: Write the failing test**

Mirror Task 4's test in `SubflowFaultServiceTests`: set up a faulted-subflow input whose parent resume fails (mock `ExecuteTransitionAsync` → `Result<TransitionOutput>.Fail(Error.Conflict(WorkflowErrorCodes.InstanceLockConflict, "lock", "conflict"))`), mock `FindWithAllCorrelationsAsync` to return a fresh clone with the completed correlation (same `CloneParentWithCompletedCorrelation` helper adapted to this file's `CreateParentInstance`), and assert:

```csharp
        _instanceRepository.Verify(
            x => x.FindWithAllCorrelationsAsync(parentInstance.Id, It.IsAny<CancellationToken>()),
            Times.Once);
        reloaded.FindCorrelationBySubInstanceId(subInstanceId)!.IsCompleted.ShouldBeFalse();
        _instanceRepository.Verify(
            x => x.UpdateAsync(reloaded, true, It.IsAny<CancellationToken>()),
            Times.Once);
```

Use this file's existing helpers (`CreateParentInstance`, `CreateParentWorkflow`, `SetupParent`, `CreateInput`, `CreateService`) for arrangement; the exception type thrown by the fault path is `SubflowCompletionException` (same as completion service — see `SubflowFaultService.ResumePipelineAsync`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~SubflowFaultServiceTests"`
Expected: new test FAILS — `FindWithAllCorrelationsAsync` never called.

- [ ] **Step 3: Implement**

Replace `RevertCorrelationInNewUowAsync` in `SubflowFaultService.cs`:

```csharp
    private async Task RevertCorrelationInNewUowAsync(
        Instance parentInstance,
        Guid subInstanceId,
        Guid parentInstanceId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var revertUow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

            // S9 isolation rule: reload with ALL correlations (completed included) so the
            // revert operates on a tracked entity and cannot silently no-op.
            var tracked = await instanceRepository.FindWithAllCorrelationsAsync(parentInstanceId, cancellationToken)
                          ?? parentInstance;

            var correlation = tracked.RevertCorrelation(subInstanceId);
            if (correlation == null)
            {
                logger.SubFlowCorrelationRevertTargetMissing(subInstanceId, parentInstanceId);
            }
            else
            {
                logger.SubFlowCorrelationReverted(subInstanceId, parentInstanceId);
                await instanceRepository.UpdateAsync(tracked, true, cancellationToken);
            }

            await revertUow.CommitAsync(cancellationToken);
        }
        catch (Exception revertEx)
        {
            logger.SubFlowCompletionFailed(revertEx, subInstanceId, parentInstanceId);
        }
    }
```

- [ ] **Step 4: Run tests**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~SubflowFaultServiceTests"`
Expected: ALL PASS.

- [ ] **Step 5: Commit**

```bash
git add src/BBT.Workflow.Application/SubFlow/Services/SubflowFaultService.cs \
        test/BBT.Workflow.Application.Tests/SubFlow/SubflowFaultServiceTests.cs
git commit -m "fix(subflow): fault-path correlation revert reloads tracked entity with all correlations"
```

---

### Task 6: Full verification

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build`
Expected: 0 errors. (If PostSharp/netstandard errors appear on macOS, run `./scripts/setup-netstandard-ref.sh` first.)

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test`
Expected: ALL PASS. Pay attention to `TransitionPipelineTests` (uses `MarkAsSubFlowResume()` at line 268 — must still compile via the default parameter) and any mocks of `IInstanceRepository` that now need the new interface member (Moq handles unimplemented members automatically; only strict mocks would need setup).

- [ ] **Step 3: Manual scenario check (optional but recommended)**

With Docker infra up (`cd etc/docker && ./run-docker.sh`), start a `sync=true` instance of a flow with two chained blocking subflow states (the production repro was `morph-idm/transaction-limit-verify`). Verify:
- No `Failed to acquire lock ... :resume` warnings in logs.
- Parent finishes `Completed`/`Active` — never left `Busy` after the response returns.
- Redis lock keys observed during the run include `...:resume:{subId:N}` (per-sub) instead of a single shared `...:resume`.

- [ ] **Step 4: Commit any leftover fixes and prepare the PR**

```bash
git add -A && git commit -m "test: adjust remaining suites for per-sub resume lock key" # only if needed
```

---

## Out of scope (explicitly deferred)

- **Self-heal for already-stuck instances**: a recovery path in `CompletionAsync` for "correlation completed + parent Busy + never resumed" (crash between Phase 1 and revert). The revert fix prevents new occurrences; existing stuck instances need a one-off data fix.
- **`Claim for job id ... was lost` warnings**: Aether SDK job-claim lease expiring during long sync chains — needs an Aether-side TTL/heartbeat alignment (tracked separately; related to the known Aether lock-enhancement dependency).
- **Lock wait/backoff**: `TryAcquireLockAsync` stays no-wait; the per-sub key removes the self-collision, and duplicate deliveries of the same completion are *meant* to fail fast.
