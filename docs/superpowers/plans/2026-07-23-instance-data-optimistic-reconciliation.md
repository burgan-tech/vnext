# InstanceData Optimistic Reconciliation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Preserve snapshot-based, latest-only pipeline performance while atomically rebasing concurrent `ScriptContext` data contributions onto the current `InstanceData` head, with five bounded reconciliation attempts and no task or pipeline restart.

**Architecture:** A tracked `Instance` snapshot records the original ordered `AddData` inputs without changing the existing merge path. A shared application service replays those inputs through `Instance.AddData` and asks a schema-aware PostgreSQL repository to compare-and-append the whole batch inside the current transition UoW; conflicts refresh only the latest row and retry the data operation. The three task phases use one applicator, while the legacy row replay remains behind a disabled-by-default rollout flag.

**Tech Stack:** .NET 10, C#, EF Core, Npgsql/PostgreSQL 16, Aether `Result`, xUnit, Shouldly, NSubstitute, Testcontainers.PostgreSql, prometheus-net.

## Global Constraints

- Preserve `JsonData.Merge`, `InstanceData.NewVersion`, scalar source-wins, recursive object merge, whole-array replacement, null, deduplication, semantic-version, and history-sequence behavior exactly.
- Do not introduce a pipeline-duration, distributed, or per-`InstanceData` application lock.
- Do not rerun a pipeline step, task, script, HTTP/Dapr call, or notification during reconciliation.
- The maximum reconciliation attempt count is the code constant `5`; the initial conditional append is attempt one.
- Retry only expected-head conflicts and explicitly mapped PostgreSQL serialization conflicts. Do not retry invalid JSON, schema validation, idempotency violations, cancellation, authorization, or connectivity failures.
- Keep contribution IDs stable across attempts and apply contributions in original call order.
- Perform expected-head validation and all inserts atomically in the existing transition UoW; never create or commit an independent transaction.
- Preserve `IsDataPartiallyLoaded=true` after synchronization and avoid duplicate EF inserts or accidental deletes.
- Preserve schema isolation through `ICurrentSchema`; never depend on the caller connection's session `search_path`.
- Do not log JSON payloads or use instance IDs, data IDs, ETags, or trace IDs as metric labels.
- `AddDataWithVersion` and explicit older-version-line writes remain full-history operations and are not reconciled.
- Before the first build on macOS/Linux run `./scripts/setup-netstandard-ref.sh`.

---

## File Map

- `src/BBT.Workflow.Domain/Instances/InstanceDataReconciliationModels.cs`: immutable baseline, contribution, change-set, prepared-row, repository-result, and repository-contract types.
- `src/BBT.Workflow.Domain/Instances/InstanceDataChangeTracker.cs`: transient ordered journal and acknowledgement lifecycle.
- `src/BBT.Workflow.Domain/Instances/Instance.cs`: tracked snapshot creation, `AddData` journaling, replay seeding, and partial-list synchronization.
- `src/BBT.Workflow.Domain/Scripting/Models.cs`: tracked refresh and isolated parallel-branch journal handling.
- `src/BBT.Workflow.Domain/Scripting/Factory/Services/ScriptContextBuilder.cs`: tracked snapshots for application-built script contexts.
- `src/BBT.Workflow.Application/Execution/Transitions/Services/InstanceDataReconciliationService.cs`: bounded fast-path/rebase algorithm.
- `src/BBT.Workflow.Application/Execution/Transitions/Services/ScriptDataChangeApplicator.cs`: feature selection, context synchronization, acknowledgement, and non-data mutations.
- `src/BBT.Workflow.Infrastructure/Instances/EfCoreInstanceRepository.cs`: latest-head projection and conditional raw-SQL batch append using the ambient EF transaction.
- `src/BBT.Workflow.Infrastructure/Migrations/20260723120000_AddInstanceDataConditionalBatchAppend.cs`: schema-local atomic append function.
- `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/RunOnExecuteTasksStep.cs`, `RunOnExitTasksStep.cs`, `RunOnEntryTasksStep.cs`: one applicator call per completed task phase.
- Monitoring, logging, options, registration, and tests listed in the tasks below complete rollout and verification.

---

### Task 1: Add the transient contribution journal to the domain

**Files:**
- Create: `src/BBT.Workflow.Domain/Instances/InstanceDataReconciliationModels.cs`
- Create: `src/BBT.Workflow.Domain/Instances/InstanceDataChangeTracker.cs`
- Modify: `src/BBT.Workflow.Domain/Instances/Instance.cs:161-174,327-368,1008-1039`
- Modify: `src/BBT.Workflow.Domain/Instances/InstanceData.cs:125-141`
- Modify: `src/BBT.Workflow.Domain/BBT.Workflow.Domain.csproj:27-28`
- Test: `test/BBT.Workflow.Domain.Tests/Instances/InstanceDataChangeTrackerTests.cs`

**Interfaces:**
- Consumes: existing `Instance.AddData(Guid, JsonData, VersionStrategy?)`, `InstanceData.CreateSnapshot()`, and `InstanceData.NewVersion(...)` behavior.
- Produces: `Instance.CreateTrackedDataSnapshot()`, `Instance.GetPendingDataChangeSet()`, `Instance.AcknowledgeDataChanges(InstanceData)`, `Instance.CreateReconciliationSnapshot(InstanceDataHead)`, and `Instance.SynchronizePartiallyLoadedData(IReadOnlyList<InstanceData>)`.

- [ ] **Step 1: Write failing domain tests for tracking, order, deduplication, partial-load preservation, and acknowledgement**

```csharp
public sealed class InstanceDataChangeTrackerTests
{
    [Fact]
    public void Tracked_snapshot_should_record_only_successful_new_AddData_inputs_in_order()
    {
        var instance = InstanceTestFactory.CreateWithData("{\"base\":1}");
        instance.MarkDataPartiallyLoaded();
        var snapshot = instance.CreateTrackedDataSnapshot();

        var firstId = Guid.NewGuid();
        var duplicateId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        snapshot.AddData(firstId, new JsonData("{\"left\":2}"), VersionStrategy.IncreasePatch);
        snapshot.AddData(duplicateId, new JsonData("{\"base\":1,\"left\":2}"), VersionStrategy.IncreasePatch);
        snapshot.AddData(secondId, new JsonData("{\"right\":3}"), VersionStrategy.IncreaseMinor);

        var changes = snapshot.GetPendingDataChangeSet().ShouldNotBeNull();
        snapshot.IsDataPartiallyLoaded.ShouldBeTrue();
        changes.Baseline.DataId.ShouldBe(instance.LatestData!.Id);
        changes.Contributions.Select(x => x.DataId).ShouldBe([firstId, secondId]);
        changes.Contributions.Select(x => x.Order).ShouldBe([0, 1]);
        changes.Contributions.Select(x => x.VersionStrategy)
            .ShouldBe([VersionStrategy.IncreasePatch, VersionStrategy.IncreaseMinor]);
        snapshot.Data.Json.ShouldBe("{\"base\":1,\"left\":2,\"right\":3}");
    }

    [Fact]
    public void Ordinary_snapshot_should_not_create_a_change_set()
    {
        var snapshot = InstanceTestFactory.CreateWithData("{\"value\":1}").CreateSnapshot();
        snapshot.AddData(Guid.NewGuid(), new JsonData("{\"value\":2}"));
        snapshot.GetPendingDataChangeSet().ShouldBeNull();
    }

    [Fact]
    public void Acknowledge_should_clear_entries_and_advance_baseline()
    {
        var snapshot = InstanceTestFactory.CreateWithData("{\"value\":1}").CreateTrackedDataSnapshot();
        var persisted = snapshot.AddData(Guid.NewGuid(), new JsonData("{\"value\":2}"));

        snapshot.AcknowledgeDataChanges(persisted);

        snapshot.GetPendingDataChangeSet().ShouldBeNull();
        snapshot.AddData(Guid.NewGuid(), new JsonData("{\"value\":3}"));
        snapshot.GetPendingDataChangeSet()!.Baseline.DataId.ShouldBe(persisted.Id);
    }
}
```

- [ ] **Step 2: Run the focused test and confirm the API is missing**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter FullyQualifiedName~InstanceDataChangeTrackerTests`

Expected: FAIL to compile because `CreateTrackedDataSnapshot`, `GetPendingDataChangeSet`, and `AcknowledgeDataChanges` do not exist.

- [ ] **Step 3: Add the immutable reconciliation contracts**

```csharp
namespace BBT.Workflow.Instances;

public sealed record InstanceDataBaseline(Guid DataId, string ETag, string Version, long VersionNo);

public sealed record InstanceDataContribution(
    Guid DataId,
    JsonData Input,
    VersionStrategy VersionStrategy,
    int Order);

public sealed record InstanceDataChangeSet(
    Guid InstanceId,
    InstanceDataBaseline? Baseline,
    IReadOnlyList<InstanceDataContribution> Contributions);

public sealed record InstanceDataHead(
    Guid DataId,
    string ETag,
    string Version,
    long VersionNo,
    int HistorySequence,
    string DataHash,
    JsonData Data,
    DateTime EnteredAt);

public sealed record PreparedInstanceData(
    Guid DataId,
    string Version,
    int HistorySequence,
    string ETag,
    string DataHash,
    JsonData Data,
    DateTime EnteredAt,
    bool IsLatest);

public enum ConditionalAppendStatus { Applied, NoChange, Conflict }

public sealed record ConditionalAppendResult(
    ConditionalAppendStatus Status,
    InstanceData? LatestData,
    IReadOnlyList<InstanceData> AppendedData,
    BBT.Aether.Results.Error? Error = null,
    InstanceDataHead? ObservedHead = null);

public interface IInstanceDataConcurrencyRepository
{
    Task<InstanceDataHead?> GetLatestDataHeadAsync(Guid instanceId, CancellationToken cancellationToken);

    Task<ConditionalAppendResult> TryAppendDataAsync(
        Guid instanceId,
        Guid? expectedLatestDataId,
        string? expectedLatestEtag,
        IReadOnlyList<PreparedInstanceData> data,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Implement the tracker and wire it into `Instance.AddData` after deduplication**

```csharp
internal sealed class InstanceDataChangeTracker(InstanceData? baseline)
{
    private readonly List<InstanceDataContribution> _contributions = [];
    private InstanceDataBaseline? _baseline = baseline is null ? null : ToBaseline(baseline);

    public void Record(Guid id, JsonData input, VersionStrategy strategy) =>
        _contributions.Add(new(id, new JsonData(input.Json), strategy, _contributions.Count));

    public InstanceDataChangeSet? GetChangeSet(Guid instanceId) =>
        _contributions.Count == 0 ? null : new(instanceId, _baseline, _contributions.ToArray());

    public void Acknowledge(InstanceData latest)
    {
        _contributions.Clear();
        _baseline = ToBaseline(latest);
    }

    private static InstanceDataBaseline ToBaseline(InstanceData data) =>
        new(data.Id, data.ETag, data.Version, data.VersionNo);
}
```

Add `_dataChangeTracker`, copy `IsDataPartiallyLoaded` in `CreateSnapshot`, and add:

```csharp
public Instance CreateTrackedDataSnapshot()
{
    var snapshot = CreateSnapshot();
    snapshot._dataChangeTracker = new InstanceDataChangeTracker(snapshot.LatestData);
    return snapshot;
}

public InstanceDataChangeSet? GetPendingDataChangeSet() =>
    _dataChangeTracker?.GetChangeSet(Id);

public void AcknowledgeDataChanges(InstanceData latestData) =>
    _dataChangeTracker?.Acknowledge(latestData);

internal Instance CreateReconciliationSnapshot(InstanceDataHead? head)
{
    var snapshot = CreateSnapshot();
    snapshot._dataList.Clear();
    if (head is not null)
        snapshot._dataList.Add(InstanceData.Rehydrate(Id, head));
    snapshot.IsDataPartiallyLoaded = true;
    return snapshot;
}

internal void SynchronizePartiallyLoadedData(IReadOnlyList<InstanceData> persistedData)
{
    if (!IsDataPartiallyLoaded)
        throw new InvalidOperationException("Reconciliation synchronization requires a latest-only aggregate.");
    _dataList.Clear();
    foreach (var data in persistedData.OrderBy(x => x.VersionNo))
        _dataList.Add(data);
}
```

Add a test for an instance created without attributes: `CreateTrackedDataSnapshot()` must succeed,
`GetPendingDataChangeSet()` must remain `null`, and no persistence is requested. If a later task
calls `AddData`, the resulting change set has `Baseline == null`; this represents a compare-and-set
whose expected database head is also null, not a synthetic initial row.

Add an exact rehydration factory inside `InstanceData`; this prevents a fresh ETag or timestamp from
being invented when a conflict refresh deduplicates to the already-persisted head:

```csharp
internal static InstanceData Rehydrate(Guid instanceId, InstanceDataHead head) => new()
{
    Id = head.DataId,
    InstanceId = instanceId,
    Version = head.Version,
    HistorySequence = head.HistorySequence,
    VersionNo = head.VersionNo,
    IsLatest = true,
    ETag = head.ETag,
    DataHash = head.DataHash,
    Data = new JsonData(head.Data.Json),
    EnteredAt = head.EnteredAt
};
```

In `AddData`, resolve the strategy once and record only when a new row was appended:

```csharp
var resolvedStrategy = versionStrategy ?? VersionStrategy.None;
// existing NewVersion/new InstanceData code remains unchanged apart from using resolvedStrategy
_dataList.Add(newData);
_dataChangeTracker?.Record(id, inputData, resolvedStrategy);
return newData;
```

Add `<InternalsVisibleTo Include="BBT.Workflow.Application" />` to the domain project so the application service can seed and synchronize replay snapshots without making those methods public.

- [ ] **Step 5: Run the tracker tests and all existing InstanceData semantic tests**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter "FullyQualifiedName~InstanceDataChangeTrackerTests|FullyQualifiedName~InstanceDataLatestInvariantTests|FullyQualifiedName~InstanceDataTests"`

Expected: PASS; existing merge/version tests remain unchanged.

- [ ] **Step 6: Commit the domain journal**

```bash
git add src/BBT.Workflow.Domain/Instances/InstanceDataReconciliationModels.cs src/BBT.Workflow.Domain/Instances/InstanceDataChangeTracker.cs src/BBT.Workflow.Domain/Instances/Instance.cs src/BBT.Workflow.Domain/Instances/InstanceData.cs src/BBT.Workflow.Domain/BBT.Workflow.Domain.csproj test/BBT.Workflow.Domain.Tests/Instances/InstanceDataChangeTrackerTests.cs
git commit -m "feat: track instance data contributions"
```

---

### Task 2: Make every application-built ScriptContext use tracked snapshots

**Files:**
- Modify: `src/BBT.Workflow.Domain/Scripting/Models.cs:515-528,610-676`
- Modify: `src/BBT.Workflow.Domain/Scripting/Factory/Services/ScriptContextBuilder.cs:91-99,292-309`
- Test: `test/BBT.Workflow.Domain.Tests/Scripting/ScriptContextTests.cs`

**Interfaces:**
- Consumes: `Instance.CreateTrackedDataSnapshot()` and the journal lifecycle from Task 1.
- Produces: tracked initial contexts, tracked refreshes, isolated parallel branch journals, and deterministic parent contributions.

- [ ] **Step 1: Add failing tests for builder, refresh, and parallel branches**

```csharp
[Fact]
public async Task Builder_should_freeze_a_tracked_snapshot_without_mutating_live_instance()
{
    var live = CreateInstanceWithData("{\"value\":1}");
    var context = await CreateBuilder().WithInstance(live).BuildAsync();
    context.Instance!.AddData(Guid.NewGuid(), new JsonData("{\"value\":2}"));
    context.Instance.GetPendingDataChangeSet().ShouldNotBeNull();
    live.GetPendingDataChangeSet().ShouldBeNull();
    live.Data.Json.ShouldBe("{\"value\":1}");
}

[Fact]
public void Refresh_should_discard_only_acknowledged_history_and_start_a_new_baseline()
{
    var live = CreateInstanceWithData("{\"value\":2}");
    var context = CreateScriptContext(CreateInstanceWithData("{\"value\":1}").CreateTrackedDataSnapshot());
    context.Instance!.AddData(Guid.NewGuid(), new JsonData("{\"local\":true}"));
    context.Instance.AcknowledgeDataChanges(context.Instance.LatestData!);
    context.RefreshInstance(live);
    context.Instance!.GetPendingDataChangeSet().ShouldBeNull();
    context.Instance.Data.Json.ShouldBe("{\"value\":2}");
}

[Fact]
public void Parallel_branch_merge_should_create_one_parent_contribution_in_merge_order()
{
    var parent = CreateScriptContext(CreateInstanceWithData("{\"base\":1}").CreateTrackedDataSnapshot());
    var first = parent.CreateParallelBranch();
    var second = parent.CreateParallelBranch();
    first.Instance!.AddData(Guid.NewGuid(), new JsonData("{\"first\":1}"));
    second.Instance!.AddData(Guid.NewGuid(), new JsonData("{\"second\":2}"));
    parent.MergeParallelBranch(first);
    parent.MergeParallelBranch(second);
    parent.Instance!.GetPendingDataChangeSet()!.Contributions.Count.ShouldBe(2);
    parent.Data.Json.ShouldBe("{\"base\":1,\"first\":1,\"second\":2}");
}
```

- [ ] **Step 2: Run the tests and confirm ordinary snapshots do not expose a journal**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter FullyQualifiedName~ScriptContextTests`

Expected: FAIL on the new journal assertions.

- [ ] **Step 3: Switch only application-level snapshot creation to the tracked factory**

Use `instance.CreateTrackedDataSnapshot()` in both `ScriptContextBuilder.WithInstance(Instance?)` and repository resolution. In `ScriptContext.RefreshInstance`, replace `CreateSnapshot()` with `CreateTrackedDataSnapshot()`. In `CreateParallelBranch`, use `Instance?.CreateTrackedDataSnapshot()` so a branch has a private empty journal based on its branch-start data. Keep `Builder.SetInstance` unchanged because low-level builder tests intentionally retain the supplied reference.

Keep the existing deterministic merge call intact:

```csharp
var branchData = branch.Instance?.LatestData;
if (Instance != null && branchData != null && branchData.Id != Instance.LatestData?.Id)
    Instance.AddData(Guid.NewGuid(), branchData.Data, VersionStrategy.IncreasePatch);
```

That parent `AddData` is now journaled naturally; never concatenate a branch's private journal into the parent.

- [ ] **Step 4: Run all ScriptContext and task coordinator tests**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --filter FullyQualifiedName~ScriptContextTests`

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter FullyQualifiedName~TaskCoordinatorTests`

Expected: PASS; parallel output ordering and conflict checks remain green.

- [ ] **Step 5: Commit tracked ScriptContext creation**

```bash
git add src/BBT.Workflow.Domain/Scripting/Models.cs src/BBT.Workflow.Domain/Scripting/Factory/Services/ScriptContextBuilder.cs test/BBT.Workflow.Domain.Tests/Scripting/ScriptContextTests.cs
git commit -m "feat: use tracked script context snapshots"
```

---

### Task 3: Define the explicit conflict error and bounded reconciliation service

**Files:**
- Create: `src/BBT.Workflow.Application/Execution/Transitions/Services/IInstanceDataReconciliationService.cs`
- Create: `src/BBT.Workflow.Application/Execution/Transitions/Services/InstanceDataReconciliationService.cs`
- Modify: `src/BBT.Workflow.Domain/WorkflowErrorCodes.cs:35-60`
- Modify: `src/BBT.Workflow.Domain/Logging/WorkflowErrors.cs:10-50`
- Test: `test/BBT.Workflow.Application.Tests/Execution/Transitions/Services/InstanceDataReconciliationServiceTests.cs`

**Interfaces:**
- Consumes: Task 1 change-set/repository contracts and domain replay helpers.
- Produces: `IInstanceDataReconciliationService.ApplyAsync(...)`, `InstanceDataReconciliationResult`, constant `MaxAttempts = 5`, and error code `Instance:100032`.

- [ ] **Step 1: Write repository-scripted tests for fast path, one rebase, and exhaustion**

```csharp
[Fact]
public async Task Fast_path_should_append_once_without_reading_fresh_head()
{
    var fixture = ReconciliationFixture.Create();
    fixture.Repository.TryAppendDataAsync(default, default, default!, default!, default)
        .ReturnsForAnyArgs(call => fixture.Applied(call.ArgAt<IReadOnlyList<PreparedInstanceData>>(3)));

    var result = await fixture.Service.ApplyAsync(fixture.Live, fixture.ChangeSet, CancellationToken.None);

    result.IsSuccess.ShouldBeTrue();
    result.Value!.AttemptCount.ShouldBe(1);
    result.Value.WasRebased.ShouldBeFalse();
    await fixture.Repository.DidNotReceiveWithAnyArgs().GetLatestDataHeadAsync(default, default);
}

[Fact]
public async Task One_conflict_should_refresh_and_replay_original_contribution()
{
    var fixture = ReconciliationFixture.Create(localInput: "{\"local\":2}");
    fixture.Repository.TryAppendDataAsync(default, default, default!, default!, default)
        .ReturnsForAnyArgs(
            new ConditionalAppendResult(ConditionalAppendStatus.Conflict, null, []),
            fixture.AppliedWithJson("{\"remote\":1,\"local\":2}"));
    fixture.Repository.GetLatestDataHeadAsync(fixture.Live.Id, default)
        .Returns(fixture.Head("{\"remote\":1}"));

    var result = await fixture.Service.ApplyAsync(fixture.Live, fixture.ChangeSet, CancellationToken.None);

    result.Value!.AttemptCount.ShouldBe(2);
    result.Value.WasRebased.ShouldBeTrue();
    result.Value.LatestData.Data.Json.ShouldBe("{\"remote\":1,\"local\":2}");
    await fixture.Repository.Received(1).GetLatestDataHeadAsync(fixture.Live.Id, Arg.Any<CancellationToken>());
}

[Fact]
public async Task Fifth_conflict_should_return_explicit_error_without_attempt_six()
{
    var fixture = ReconciliationFixture.Create();
    fixture.Repository.TryAppendDataAsync(default, default, default!, default!, default)
        .ReturnsForAnyArgs(new ConditionalAppendResult(ConditionalAppendStatus.Conflict, null, []));
    fixture.Repository.GetLatestDataHeadAsync(default, default)
        .ReturnsForAnyArgs(fixture.Head("{\"remote\":1}"));

    var result = await fixture.Service.ApplyAsync(fixture.Live, fixture.ChangeSet, CancellationToken.None);

    result.IsSuccess.ShouldBeFalse();
    result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceDataConcurrencyConflict);
    await fixture.Repository.ReceivedWithAnyArgs(5).TryAppendDataAsync(default, default, default!, default!, default);
    await fixture.Repository.ReceivedWithAnyArgs(4).GetLatestDataHeadAsync(default, default);
}
```

The fixture must create a partially loaded live aggregate, create a tracked snapshot, call `AddData` once, and expose its exact pending change set. Its fake `Applied` result must rehydrate persisted `InstanceData` with the same contribution ID and assigned `VersionNo`.

- [ ] **Step 2: Run the service tests and verify the service/error are absent**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter FullyQualifiedName~InstanceDataReconciliationServiceTests`

Expected: FAIL to compile because the service and conflict code do not exist.

- [ ] **Step 3: Add the service contract and error**

```csharp
public interface IInstanceDataReconciliationService
{
    Task<Result<InstanceDataReconciliationResult>> ApplyAsync(
        Instance instance,
        InstanceDataChangeSet changeSet,
        CancellationToken cancellationToken);
}

public sealed record InstanceDataReconciliationResult(
    InstanceData LatestData,
    IReadOnlyList<InstanceData> AppendedData,
    int AttemptCount,
    bool WasRebased);
```

Add `public const string InstanceDataConcurrencyConflict = "Instance:100032";` and:

```csharp
public static Error InstanceDataConcurrencyConflict(Guid instanceId, int attempts) =>
    Error.Conflict(
        WorkflowErrorCodes.InstanceDataConcurrencyConflict,
        $"Instance data changed concurrently and could not be reconciled after {attempts} attempts.",
        target: instanceId.ToString());
```

- [ ] **Step 4: Implement replay and the exact five-attempt loop**

```csharp
public sealed class InstanceDataReconciliationService(
    IInstanceDataConcurrencyRepository repository) : IInstanceDataReconciliationService
{
    internal const int MaxAttempts = 5;

    public async Task<Result<InstanceDataReconciliationResult>> ApplyAsync(
        Instance instance,
        InstanceDataChangeSet changeSet,
        CancellationToken cancellationToken)
    {
        InstanceDataHead? head = changeSet.Baseline is null ? null : ToHead(instance.LatestData!);
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (attempt > 1)
            {
                head = await repository.GetLatestDataHeadAsync(instance.Id, cancellationToken);
            }

            var working = instance.CreateReconciliationSnapshot(head);
            var appended = new List<InstanceData>();
            foreach (var contribution in changeSet.Contributions.OrderBy(x => x.Order))
            {
                var before = working.LatestData;
                var after = working.AddData(
                    contribution.DataId,
                    new JsonData(contribution.Input.Json),
                    contribution.VersionStrategy);
                if (after.Id != before?.Id)
                    appended.Add(after);
            }

            if (appended.Count == 0)
                return Result<InstanceDataReconciliationResult>.Ok(
                    new(working.LatestData!, [], attempt, attempt > 1));

            var appendResult = await repository.TryAppendDataAsync(
                instance.Id,
                head?.DataId,
                head?.ETag,
                appended.Select(ToPrepared).ToArray(),
                cancellationToken);

            if (appendResult.Error is not null)
                return Result<InstanceDataReconciliationResult>.Fail(appendResult.Error);

            if (appendResult.Status is ConditionalAppendStatus.Applied or ConditionalAppendStatus.NoChange)
                return Result<InstanceDataReconciliationResult>.Ok(
                    new(appendResult.LatestData!, appendResult.AppendedData, attempt, attempt > 1));
        }

        return Result<InstanceDataReconciliationResult>.Fail(
            WorkflowErrors.InstanceDataConcurrencyConflict(instance.Id, MaxAttempts));
    }

    private static InstanceDataHead ToHead(InstanceData data) =>
        new(data.Id, data.ETag, data.Version, data.VersionNo, data.HistorySequence,
            data.DataHash, new JsonData(data.Data.Json), data.EnteredAt);

    private static PreparedInstanceData ToPrepared(InstanceData data) =>
        new(data.Id, data.Version, data.HistorySequence, data.ETag, data.DataHash,
            new JsonData(data.Data.Json), data.EnteredAt, data.IsLatest);
}
```

- [ ] **Step 5: Run the reconciliation unit tests**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter FullyQualifiedName~InstanceDataReconciliationServiceTests`

Expected: PASS, including exactly five append calls and four refresh reads on exhaustion.

- [ ] **Step 6: Commit the bounded algorithm**

```bash
git add src/BBT.Workflow.Application/Execution/Transitions/Services/IInstanceDataReconciliationService.cs src/BBT.Workflow.Application/Execution/Transitions/Services/InstanceDataReconciliationService.cs src/BBT.Workflow.Domain/WorkflowErrorCodes.cs src/BBT.Workflow.Domain/Logging/WorkflowErrors.cs test/BBT.Workflow.Application.Tests/Execution/Transitions/Services/InstanceDataReconciliationServiceTests.cs
git commit -m "feat: reconcile concurrent instance data changes"
```

---

### Task 4: Add the schema-local atomic PostgreSQL batch operation

**Files:**
- Create: `src/BBT.Workflow.Infrastructure/Migrations/20260723120000_AddInstanceDataConditionalBatchAppend.cs`
- Create: `src/BBT.Workflow.Infrastructure/Migrations/20260723120000_AddInstanceDataConditionalBatchAppend.Designer.cs`
- Test: `test/BBT.Workflow.Infrastructure.Tests/Domains/Instances/InstanceDataConditionalAppendFunctionTests.cs`

**Interfaces:**
- Consumes: current `set_instance_data_version_and_latest()` advisory-lock trigger and `InstancesData` column layout.
- Produces: schema-local `try_append_instance_data_batch(uuid, uuid, text, jsonb)` returning status plus persisted rows.

- [ ] **Step 1: Add failing real-PostgreSQL tests for atomicity, idempotency, and schema isolation**

```csharp
[Fact]
public async Task Stale_expected_head_should_return_conflict_without_writing()
{
    var baseline = await SeedBaselineAsync("tenant_a", "{\"base\":1}");
    await InsertConcurrentHeadAsync("tenant_a", baseline.InstanceId, "{\"remote\":2}");
    var result = await CallFunctionAsync("tenant_a", baseline, Rows(("{\"local\":3}", true)));
    result.Single().Status.ShouldBe("conflict");
    (await ReadRowsAsync("tenant_a", baseline.InstanceId)).Count.ShouldBe(2);
}

[Fact]
public async Task Middle_row_failure_should_roll_back_complete_batch()
{
    var baseline = await SeedBaselineAsync("tenant_a", "{\"base\":1}");
    var rows = Rows(("{\"a\":1}", false), ("invalid-version-over-max-length", false), ("{\"c\":3}", true));
    await Should.ThrowAsync<PostgresException>(() => CallFunctionAsync("tenant_a", baseline, rows));
    var persisted = await ReadRowsAsync("tenant_a", baseline.InstanceId);
    persisted.Count.ShouldBe(1);
    persisted.Single().IsLatest.ShouldBeTrue();
}

[Fact]
public async Task Repeated_identical_batch_should_return_no_change_and_partial_replay_should_fail()
{
    var baseline = await SeedBaselineAsync("tenant_a", "{\"base\":1}");
    var rows = Rows(("{\"a\":1}", false), ("{\"b\":2}", true));
    (await CallFunctionAsync("tenant_a", baseline, rows)).First().Status.ShouldBe("applied");
    (await CallFunctionAsync("tenant_a", baseline, rows)).First().Status.ShouldBe("no_change");
    await Should.ThrowAsync<PostgresException>(() => CallFunctionAsync("tenant_a", baseline, rows.Take(1).ToArray()));
}

[Fact]
public async Task Same_instance_id_in_two_schemas_should_remain_isolated()
{
    var id = Guid.NewGuid();
    var a = await SeedBaselineAsync("tenant_a", "{\"tenant\":\"a\"}", id);
    var b = await SeedBaselineAsync("tenant_b", "{\"tenant\":\"b\"}", id);
    await CallFunctionAsync("tenant_a", a, Rows(("{\"changed\":true}", true)));
    (await ReadLatestAsync("tenant_b", id)).Data.ShouldBe("{\"tenant\":\"b\"}");
}
```

Use `PostgreSqlBuilder().WithImage("postgres:16-alpine")`; create both schemas, apply the current trigger to each, then apply the new migration SQL to each schema. The test helper must call the function with an explicitly quoted schema and parameterized values.

- [ ] **Step 2: Run the integration test and confirm the function is missing**

Run: `dotnet test test/BBT.Workflow.Infrastructure.Tests --filter FullyQualifiedName~InstanceDataConditionalAppendFunctionTests`

Expected: FAIL with PostgreSQL `42883` (function does not exist).

- [ ] **Step 3: Implement the migration function with one advisory-lock boundary**

The `Up` migration must create this function in the current migration schema. `MultiSchemaNpgsqlMigrationsSqlGenerator` prefixes each raw migration operation with the tenant schema and the function captures that value with `SET search_path FROM CURRENT`; therefore runtime calls do not depend on pooled connection state. Use the captured schema through `format('%I', current_schema())`:

```sql
CREATE OR REPLACE FUNCTION try_append_instance_data_batch(
    p_instance_id uuid,
    p_expected_data_id uuid,
    p_expected_etag text,
    p_rows jsonb)
RETURNS TABLE (
    "Status" text, "Id" uuid, "Version" text, "VersionNo" bigint,
    "HistorySequence" integer, "ETag" text, "DataHash" text,
    "Data" jsonb, "EnteredAt" timestamp without time zone, "IsLatest" boolean)
LANGUAGE plpgsql
SET search_path FROM CURRENT
AS $$
DECLARE
    v_schema text := current_schema();
    v_latest_id uuid;
    v_latest_etag text;
    v_input_count integer := jsonb_array_length(p_rows);
    v_existing_count integer;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext(p_instance_id::text));

    EXECUTE format(
        'SELECT "Id", "ETag" FROM %I."InstancesData" WHERE "InstanceId"=$1 AND "IsLatest"=TRUE',
        v_schema)
    INTO v_latest_id, v_latest_etag USING p_instance_id;

    EXECUTE format(
        'SELECT count(*) FROM %I."InstancesData" d '
        'JOIN jsonb_to_recordset($2) r("DataId" uuid, "Version" text, "HistorySequence" integer, '
        '"ETag" text, "DataHash" text, "Data" jsonb, "EnteredAt" timestamp, "IsLatest" boolean) '
        'ON d."Id"=r."DataId" AND d."InstanceId"=$1', v_schema)
    INTO v_existing_count USING p_instance_id, p_rows;

    IF v_existing_count = v_input_count THEN
        IF EXISTS (
            SELECT 1 FROM jsonb_to_recordset(p_rows) r(
                "DataId" uuid, "Version" text, "HistorySequence" integer, "ETag" text,
                "DataHash" text, "Data" jsonb, "EnteredAt" timestamp, "IsLatest" boolean)
            WHERE NOT EXISTS (
                SELECT 1 FROM "InstancesData" d
                WHERE d."InstanceId"=p_instance_id AND d."Id"=r."DataId"
                  AND d."Version"=r."Version" AND d."HistorySequence"=r."HistorySequence"
                  AND d."ETag"=r."ETag" AND d."DataHash"=r."DataHash"
                  AND d."Data"=r."Data"))
        THEN
            RAISE EXCEPTION 'instance_data_idempotency_violation' USING ERRCODE='P0001';
        END IF;
        RETURN QUERY EXECUTE format(
            'SELECT ''no_change'', d."Id", d."Version", d."VersionNo", d."HistorySequence", '
            'd."ETag", d."DataHash", d."Data", d."EnteredAt", d."IsLatest" '
            'FROM %I."InstancesData" d JOIN jsonb_to_recordset($2) WITH ORDINALITY '
            'r("DataId" uuid, ord bigint) ON d."Id"=r."DataId" '
            'WHERE d."InstanceId"=$1 ORDER BY r.ord', v_schema)
            USING p_instance_id, p_rows;
        RETURN;
    ELSIF v_existing_count > 0 THEN
        RAISE EXCEPTION 'instance_data_partial_idempotency_violation' USING ERRCODE='P0001';
    END IF;

    IF v_latest_id IS DISTINCT FROM p_expected_data_id
       OR v_latest_etag IS DISTINCT FROM p_expected_etag THEN
        RETURN QUERY EXECUTE format(
            'SELECT ''conflict'', d."Id", d."Version", d."VersionNo", d."HistorySequence", '
            'd."ETag", d."DataHash", d."Data", d."EnteredAt", d."IsLatest" '
            'FROM %I."InstancesData" d WHERE d."InstanceId"=$1 AND d."IsLatest"=TRUE', v_schema)
            USING p_instance_id;
        RETURN;
    END IF;

    RETURN QUERY EXECUTE format(
        'INSERT INTO %I."InstancesData" '
        '("Id","InstanceId","Version","HistorySequence","ETag","DataHash","Data","EnteredAt","IsLatest") '
        'SELECT r."DataId", $1, r."Version", r."HistorySequence", r."ETag", r."DataHash", '
        'r."Data", r."EnteredAt", r."IsLatest" '
        'FROM jsonb_to_recordset($2) WITH ORDINALITY r('
        '"DataId" uuid,"Version" text,"HistorySequence" integer,"ETag" text,"DataHash" text,'
        '"Data" jsonb,"EnteredAt" timestamp,"IsLatest" boolean,ord bigint) ORDER BY r.ord '
        'RETURNING ''applied'', "Id", "Version", "VersionNo", "HistorySequence", '
        '"ETag", "DataHash", "Data", "EnteredAt", "IsLatest"', v_schema)
        USING p_instance_id, p_rows;
END;
$$;
```

Qualify the nested idempotency comparison with dynamic SQL too; the static `"InstancesData"` shown inside the logical block must be emitted through `format('%I', v_schema)` in the migration. The function-local captured path is fixed at migration time and is not the caller session path, so it remains safe with PgBouncer transaction pooling. `Down` must execute `DROP FUNCTION IF EXISTS try_append_instance_data_batch(uuid, uuid, text, jsonb);`.

Generate the designer with the existing model snapshot without changing `WorkflowDbContextModelSnapshot` because this migration adds no EF model object.

- [ ] **Step 4: Run the PostgreSQL function tests**

Run: `dotnet test test/BBT.Workflow.Infrastructure.Tests --filter FullyQualifiedName~InstanceDataConditionalAppendFunctionTests`

Expected: PASS for conflict/no-write, whole-batch rollback, idempotency, monotonic `VersionNo`, one latest row, and schema isolation.

- [ ] **Step 5: Commit the atomic database boundary**

```bash
git add src/BBT.Workflow.Infrastructure/Migrations/20260723120000_AddInstanceDataConditionalBatchAppend.cs src/BBT.Workflow.Infrastructure/Migrations/20260723120000_AddInstanceDataConditionalBatchAppend.Designer.cs test/BBT.Workflow.Infrastructure.Tests/Domains/Instances/InstanceDataConditionalAppendFunctionTests.cs
git commit -m "feat: add atomic instance data batch append"
```

---

### Task 5: Implement the schema-aware EF repository path and state synchronization

**Files:**
- Modify: `src/BBT.Workflow.Infrastructure/Instances/EfCoreInstanceRepository.cs:23-36,1482-1485`
- Modify: `src/BBT.Workflow.Infrastructure/Microsoft/Extensions/DependencyInjection/WorkflowInfrastructureModuleServiceCollectionExtensions.cs:86-96`
- Test: `test/BBT.Workflow.Infrastructure.Tests/Domains/Instances/EfCoreInstanceDataConcurrencyRepositoryTests.cs`

**Interfaces:**
- Consumes: `IInstanceDataConcurrencyRepository`, the Task 4 function, `IAetherDbContextProvider<WorkflowDbContext>`, and the ambient EF transaction.
- Produces: no-tracking latest projection, parameterized function call, returned `InstanceData` entities attached as `Unchanged`, and one shared repository instance per DI scope.

- [ ] **Step 1: Add failing repository tests for query count, transaction requirement, and EF state**

```csharp
[Fact]
public async Task Fast_append_should_use_current_transaction_and_attach_returned_rows_as_unchanged()
{
    await using var context = CreateContext("tenant_a");
    await using var transaction = await context.Database.BeginTransactionAsync();
    var repository = CreateRepository(context, "tenant_a");
    var baseline = await SeedBaselineAsync(context);

    var result = await repository.TryAppendDataAsync(
        baseline.InstanceId, baseline.Id, baseline.ETag, [PreparedAfter(baseline)], CancellationToken.None);

    result.Status.ShouldBe(ConditionalAppendStatus.Applied);
    context.Entry(result.AppendedData.Single()).State.ShouldBe(EntityState.Unchanged);
    context.ChangeTracker.Entries<InstanceData>().Count(x => x.State == EntityState.Added).ShouldBe(0);
    await transaction.RollbackAsync();
    (await ReadRowCountInNewContext(baseline.InstanceId)).ShouldBe(1);
}

[Fact]
public async Task Latest_head_read_should_bypass_the_stale_tracked_entity()
{
    await using var context = CreateContext("tenant_a");
    var stale = await context.InstancesData.SingleAsync(x => x.IsLatest);
    await AdvanceHeadInSeparateContext(stale.InstanceId);
    var repository = CreateRepository(context, "tenant_a");
    var head = await repository.GetLatestDataHeadAsync(stale.InstanceId, CancellationToken.None);
    head!.DataId.ShouldNotBe(stale.Id);
}

[Fact]
public async Task Append_without_ambient_transaction_should_fail_before_calling_function()
{
    await using var context = CreateContext("tenant_a");
    var repository = CreateRepository(context, "tenant_a");
    await Should.ThrowAsync<InvalidOperationException>(() => repository.TryAppendDataAsync(
        Guid.NewGuid(), Guid.NewGuid(), "etag", [], CancellationToken.None));
}
```

- [ ] **Step 2: Run the repository tests and confirm the interface is not implemented**

Run: `dotnet test test/BBT.Workflow.Infrastructure.Tests --filter FullyQualifiedName~EfCoreInstanceDataConcurrencyRepositoryTests`

Expected: FAIL because `EfCoreInstanceRepository` cannot be assigned to `IInstanceDataConcurrencyRepository`.

- [ ] **Step 3: Implement the projection and ambient-transaction function call**

Add `IInstanceDataConcurrencyRepository` to `EfCoreInstanceRepository` and implement:

```csharp
public async Task<InstanceDataHead?> GetLatestDataHeadAsync(Guid instanceId, CancellationToken cancellationToken)
{
    var context = await GetDbContextAsync();
    return await context.InstancesData.AsNoTracking()
        .Where(x => x.InstanceId == instanceId && x.IsLatest)
        .Select(x => new InstanceDataHead(
            x.Id, x.ETag, x.Version, x.VersionNo, x.HistorySequence,
            x.DataHash, new JsonData(x.Data.Json), x.EnteredAt))
        .SingleOrDefaultAsync(cancellationToken);
}

public async Task<ConditionalAppendResult> TryAppendDataAsync(
    Guid instanceId,
    Guid? expectedLatestDataId,
    string? expectedLatestEtag,
    IReadOnlyList<PreparedInstanceData> data,
    CancellationToken cancellationToken)
{
    var context = await GetDbContextAsync();
    var transaction = context.Database.CurrentTransaction
        ?? throw new InvalidOperationException("InstanceData reconciliation requires the ambient transition transaction.");
    var schema = SanitizeIdentifier(currentSchema.Name ?? DefaultSchemaName);
    var payload = JsonSerializer.Serialize(data, CamelCaseCompactJson);
    var connection = (Npgsql.NpgsqlConnection)context.Database.GetDbConnection();

    await using var command = connection.CreateCommand();
    command.Transaction = (Npgsql.NpgsqlTransaction)transaction.GetDbTransaction();
    command.CommandText = $"SELECT * FROM \"{schema}\".try_append_instance_data_batch(@instanceId,@expectedId,@expectedEtag,@rows)";
    command.Parameters.AddWithValue("instanceId", instanceId);
    command.Parameters.AddWithValue("expectedId", expectedLatestDataId);
    command.Parameters.AddWithValue("expectedEtag", expectedLatestEtag);
    command.Parameters.AddWithValue("rows", NpgsqlTypes.NpgsqlDbType.Jsonb, payload);

    var rows = await ReadConditionalRowsAsync(command, instanceId, cancellationToken);
    if (rows.Count == 1 && rows[0].Status == "conflict")
        return new(ConditionalAppendStatus.Conflict, null, [],
            ObservedHead: rows[0].ToHead());

    var entities = rows.Select(x => x.ToInstanceData(instanceId)).ToArray();
    var returnedIds = entities.Select(x => x.Id).ToHashSet();
    foreach (var stale in context.ChangeTracker.Entries<InstanceData>()
                 .Where(x => x.Entity.InstanceId == instanceId && !returnedIds.Contains(x.Entity.Id))
                 .ToArray())
        stale.State = EntityState.Detached;
    foreach (var entity in entities)
        context.Entry(entity).State = EntityState.Unchanged;
    var latest = entities.Single(x => x.IsLatest);
    return new(rows[0].Status == "no_change" ? ConditionalAppendStatus.NoChange : ConditionalAppendStatus.Applied,
        latest, entities);
}
```

`ReadConditionalRowsAsync` must map all returned fields, preserve database-assigned `VersionNo`, and map only PostgreSQL `40001`/`40P01` to `Conflict`. Map custom idempotency `P0001` to a non-retryable `Error.Conflict` and allow cancellation/connectivity exceptions to propagate. Detaching the stale loaded head is mandatory before the domain list is replaced; it prevents EF orphan/delete detection and prevents its stale `IsLatest=true` value from being written back. Do not open, begin, commit, or roll back a transaction here.

- [ ] **Step 4: Register one scoped concrete repository for both interfaces**

```csharp
services.AddScoped<EfCoreInstanceRepository>();
services.AddScoped<IInstanceRepository>(sp => sp.GetRequiredService<EfCoreInstanceRepository>());
services.AddScoped<IInstanceDataConcurrencyRepository>(sp => sp.GetRequiredService<EfCoreInstanceRepository>());
```

This replaces the direct `IInstanceRepository, EfCoreInstanceRepository` registration and guarantees both contracts share the same schema-aware DbContext and change tracker.

- [ ] **Step 5: Run repository and existing versioning integration tests**

Run: `dotnet test test/BBT.Workflow.Infrastructure.Tests --filter "FullyQualifiedName~EfCoreInstanceDataConcurrencyRepositoryTests|FullyQualifiedName~InstanceDataVersioningTests|FullyQualifiedName~InstanceDataConditionalAppendFunctionTests"`

Expected: PASS; rollback leaves only the baseline row, returned rows are `Unchanged`, `VersionNo` is monotonic, and exactly one row is latest.

- [ ] **Step 6: Commit repository integration**

```bash
git add src/BBT.Workflow.Infrastructure/Instances/EfCoreInstanceRepository.cs src/BBT.Workflow.Infrastructure/Microsoft/Extensions/DependencyInjection/WorkflowInfrastructureModuleServiceCollectionExtensions.cs test/BBT.Workflow.Infrastructure.Tests/Domains/Instances/EfCoreInstanceDataConcurrencyRepositoryTests.cs
git commit -m "feat: persist reconciled instance data batches"
```

---

### Task 6: Apply reconciled data once in all three task phases

**Files:**
- Create: `src/BBT.Workflow.Application/Execution/Transitions/Services/IScriptDataChangeApplicator.cs`
- Create: `src/BBT.Workflow.Application/Execution/Transitions/Services/ScriptDataChangeApplicator.cs`
- Modify: `src/BBT.Workflow.Domain/Execution/Transitions/Context/TransitionExecutionContext.cs:224-261`
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/RunOnExecuteTasksStep.cs:17-23,74-80,118-123`
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/RunOnExitTasksStep.cs:18-24,75-81,119-124`
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/RunOnEntryTasksStep.cs:17-23,73-79,117-122`
- Modify: `src/BBT.Workflow.Application/BackgroundJobs/Options/WorkflowExecutionOptions.cs:51-59`
- Test: `test/BBT.Workflow.Application.Tests/Execution/Transitions/Services/ScriptDataChangeApplicatorTests.cs`
- Test: `test/BBT.Workflow.Application.Tests/Execution/Transitions/Pipeline/Steps/TaskStepDataReconciliationTests.cs`

**Interfaces:**
- Consumes: `IInstanceDataReconciliationService`, existing legacy `ApplyScriptContextChanges`, and `WorkflowExecutionOptions`.
- Produces: `IScriptDataChangeApplicator.ApplyAsync(...)` and one result check before repository `UpdateAsync` in each task phase.

- [ ] **Step 1: Write applicator tests for success, failure, acknowledgement, data refresh, and legacy flag**

```csharp
[Fact]
public async Task Enabled_success_should_update_context_acknowledge_journal_then_apply_mutations()
{
    var fixture = ApplicatorFixture.Create(enabled: true);
    fixture.ScriptContext.Mutations.SetStage("review");
    fixture.Reconciler.ApplyAsync(default!, default!, default)
        .ReturnsForAnyArgs(Result<InstanceDataReconciliationResult>.Ok(fixture.Success));

    var result = await fixture.Applicator.ApplyAsync(fixture.Transition, fixture.ScriptContext, CancellationToken.None);

    result.IsSuccess.ShouldBeTrue();
    fixture.Transition.Data.Json.ShouldBe(fixture.Success.LatestData.Data.Json);
    fixture.Transition.Instance.Stage.ShouldBe("review");
    fixture.ScriptContext.Instance!.GetPendingDataChangeSet().ShouldBeNull();
}

[Fact]
public async Task Enabled_failure_should_leave_journal_and_mutations_unapplied()
{
    var fixture = ApplicatorFixture.Create(enabled: true);
    fixture.ScriptContext.Mutations.SetStage("review");
    fixture.Reconciler.ApplyAsync(default!, default!, default)
        .ReturnsForAnyArgs(Result<InstanceDataReconciliationResult>.Fail(
            WorkflowErrors.InstanceDataConcurrencyConflict(fixture.Transition.Instance.Id, 5)));

    var result = await fixture.Applicator.ApplyAsync(fixture.Transition, fixture.ScriptContext, CancellationToken.None);

    result.IsSuccess.ShouldBeFalse();
    fixture.ScriptContext.Instance!.GetPendingDataChangeSet().ShouldNotBeNull();
    fixture.Transition.Instance.Stage.ShouldNotBe("review");
}

[Fact]
public async Task Disabled_flag_should_use_legacy_fail_fast_row_replay()
{
    var fixture = ApplicatorFixture.Create(enabled: false);
    await fixture.Applicator.ApplyAsync(fixture.Transition, fixture.ScriptContext, CancellationToken.None);
    await fixture.Reconciler.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default);
}
```

- [ ] **Step 2: Run applicator tests and confirm the abstraction is absent**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter FullyQualifiedName~ScriptDataChangeApplicatorTests`

Expected: FAIL to compile because `IScriptDataChangeApplicator` does not exist.

- [ ] **Step 3: Add the option and implement the applicator**

```csharp
public bool EnableInstanceDataReconciliation { get; set; }
```

```csharp
public interface IScriptDataChangeApplicator
{
    Task<Result> ApplyAsync(
        TransitionExecutionContext transitionContext,
        ScriptContext scriptContext,
        CancellationToken cancellationToken);
}

public sealed class ScriptDataChangeApplicator(
    IInstanceDataReconciliationService reconciliationService,
    IOptions<WorkflowExecutionOptions> options) : IScriptDataChangeApplicator
{
    public async Task<Result> ApplyAsync(
        TransitionExecutionContext transitionContext,
        ScriptContext scriptContext,
        CancellationToken cancellationToken)
    {
        if (!options.Value.EnableInstanceDataReconciliation)
        {
            transitionContext.ApplyScriptContextChanges(scriptContext);
            return Result.Ok();
        }

        var changeSet = scriptContext.Instance?.GetPendingDataChangeSet();
        if (changeSet is not null)
        {
            var reconciled = await reconciliationService.ApplyAsync(
                transitionContext.Instance, changeSet, cancellationToken);
            if (!reconciled.IsSuccess)
                return Result.Fail(reconciled.Error);

            var value = reconciled.Value!;
            transitionContext.Instance.SynchronizePartiallyLoadedData(
                value.AppendedData.Count == 0 ? [value.LatestData] : value.AppendedData);
            transitionContext.Data = value.LatestData.Data;
            scriptContext.Instance!.AcknowledgeDataChanges(value.LatestData);
            scriptContext.RefreshInstance(transitionContext.Instance);
        }

        transitionContext.ApplyScriptContextMutations(scriptContext);
        return Result.Ok();
    }
}
```

Split the old context method so legacy data replay remains in `ApplyScriptContextChanges`, while the new public `ApplyScriptContextMutations(ScriptContext)` contains only:

```csharp
if (scriptContext.Mutations.HasChanges)
    scriptContext.Mutations.ApplyTo(Instance);
```

- [ ] **Step 4: Change each task step to call the applicator once and propagate failure**

Inject `IScriptDataChangeApplicator scriptDataChangeApplicator`. Replace both normal and boundary-path direct calls with:

```csharp
var applyResult = await scriptDataChangeApplicator.ApplyAsync(context, scriptContext, cancellationToken);
if (!applyResult.IsSuccess)
    return Result<StepOutcome>.Fail(applyResult.Error);
await instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);
```

Do this in `RunOnExecuteTasksStep`, `RunOnExitTasksStep`, and `RunOnEntryTasksStep`. Do not wrap task execution in a retry loop.

- [ ] **Step 5: Add a parameterized pipeline regression proving task execution count stays one**

```csharp
[Theory]
[InlineData(typeof(RunOnExecuteTasksStep))]
[InlineData(typeof(RunOnExitTasksStep))]
[InlineData(typeof(RunOnEntryTasksStep))]
public async Task Conflict_then_success_should_not_reexecute_tasks(Type stepType)
{
    var fixture = TaskStepFixture.Create(stepType);
    fixture.Applicator.ApplyAsync(default!, default!, default)
        .ReturnsForAnyArgs(Result.Ok());

    var result = await fixture.ExecuteAsync();

    result.IsSuccess.ShouldBeTrue();
    fixture.TaskCoordinatorExecutionCount.ShouldBe(1);
    await fixture.Applicator.ReceivedWithAnyArgs(1).ApplyAsync(default!, default!, default);
}

[Theory]
[InlineData(typeof(RunOnExecuteTasksStep))]
[InlineData(typeof(RunOnExitTasksStep))]
[InlineData(typeof(RunOnEntryTasksStep))]
public async Task Exhausted_conflict_should_fail_step_and_not_persist(Type stepType)
{
    var fixture = TaskStepFixture.Create(stepType);
    fixture.Applicator.ApplyAsync(default!, default!, default)
        .ReturnsForAnyArgs(Result.Fail(
            WorkflowErrors.InstanceDataConcurrencyConflict(fixture.Instance.Id, 5)));
    var result = await fixture.ExecuteAsync();
    result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceDataConcurrencyConflict);
    fixture.TaskCoordinatorExecutionCount.ShouldBe(1);
    await fixture.InstanceRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default, default);
}
```

- [ ] **Step 6: Run applicator and all three task-step regressions**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter "FullyQualifiedName~ScriptDataChangeApplicatorTests|FullyQualifiedName~TaskStepDataReconciliationTests"`

Expected: PASS; reconciliation attempts can vary while every task coordinator invocation remains exactly one.

- [ ] **Step 7: Commit the pipeline integration**

```bash
git add src/BBT.Workflow.Application/Execution/Transitions/Services/IScriptDataChangeApplicator.cs src/BBT.Workflow.Application/Execution/Transitions/Services/ScriptDataChangeApplicator.cs src/BBT.Workflow.Domain/Execution/Transitions/Context/TransitionExecutionContext.cs src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/RunOnExecuteTasksStep.cs src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/RunOnExitTasksStep.cs src/BBT.Workflow.Application/Execution/Transitions/Pipeline/Steps/RunOnEntryTasksStep.cs src/BBT.Workflow.Application/BackgroundJobs/Options/WorkflowExecutionOptions.cs test/BBT.Workflow.Application.Tests/Execution/Transitions/Services/ScriptDataChangeApplicatorTests.cs test/BBT.Workflow.Application.Tests/Execution/Transitions/Pipeline/Steps/TaskStepDataReconciliationTests.cs
git commit -m "feat: apply reconciled script data in task steps"
```

---

### Task 7: Add DI, structured logs, metrics, and rollout configuration

**Files:**
- Modify: `src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/PipelineServiceCollectionExtensions.cs:42-50`
- Modify: `src/BBT.Workflow.Domain/Monitoring/IWorkflowMetrics.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Monitoring/WorkflowMetrics.cs`
- Modify: `src/BBT.Workflow.Infrastructure/Monitoring/PrometheusWorkflowMetrics.cs`
- Modify: `src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs`
- Modify: `src/BBT.Workflow.Application/Execution/Transitions/Services/InstanceDataReconciliationService.cs`
- Modify: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json`
- Test: `test/BBT.Workflow.Infrastructure.Tests/Monitoring/PrometheusWorkflowMetricsTests.cs`
- Test: `test/BBT.Workflow.Application.Tests/Microsoft/Extensions/DependencyInjection/PipelineServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Consumes: Task 3/6 services and attempt outcomes.
- Produces: complete runtime registrations, low-cardinality metrics, payload-free structured events, and default-off rollout.

- [ ] **Step 1: Write failing registration and metric smoke tests**

```csharp
[Fact]
public void Pipeline_services_should_resolve_reconciliation_services()
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddOptions<WorkflowExecutionOptions>();
    services.AddPipelineServices();
    services.Any(x => x.ServiceType == typeof(IInstanceDataReconciliationService)).ShouldBeTrue();
    services.Any(x => x.ServiceType == typeof(IScriptDataChangeApplicator)).ShouldBeTrue();
}

[Fact]
public void Reconciliation_metrics_should_accept_only_approved_labels()
{
    var metrics = new PrometheusWorkflowMetrics();
    metrics.RecordInstanceDataReconciliation(
        "flow-a", "OnExecute", "applied", rebased: true,
        attempts: 2, durationSeconds: 0.01, contributions: 1, conflicts: 1);
}
```

- [ ] **Step 2: Run focused tests and confirm missing registrations/methods**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter FullyQualifiedName~PipelineServiceCollectionExtensionsTests`

Run: `dotnet test test/BBT.Workflow.Infrastructure.Tests --filter FullyQualifiedName~PrometheusWorkflowMetricsTests`

Expected: FAIL to compile on the new reconciliation metric method and fail the registration assertions.

- [ ] **Step 3: Register the two scoped application services**

```csharp
services.AddScoped<IInstanceDataReconciliationService, InstanceDataReconciliationService>();
services.AddScoped<IScriptDataChangeApplicator, ScriptDataChangeApplicator>();
```

- [ ] **Step 4: Add the metric contract and prometheus-net collectors**

```csharp
void RecordInstanceDataReconciliation(
    string flow, string pipelineStep, string result, bool rebased,
    int attempts, double durationSeconds, int contributions, int conflicts);
```

Create counters/histograms named:

```csharp
workflow_instance_data_reconciliation_total
workflow_instance_data_reconciliation_conflicts_total
workflow_instance_data_reconciliation_exhausted_total
workflow_instance_data_reconciliation_attempts
workflow_instance_data_reconciliation_duration_seconds
workflow_instance_data_reconciliation_contributions
```

Use labels only `flow`, `pipeline_step`, `result`, and `rebased`; observe numeric attempts, duration, and contribution count as histogram values, not labels. `PrometheusWorkflowMetrics.RecordInstanceDataReconciliation` increments total/conflict/exhausted counters and observes all three histograms.

- [ ] **Step 5: Add payload-free source-generated log events and call them from reconciliation**

```csharp
[LoggerMessage(EventId = 10110, Level = LogLevel.Warning,
    Message = "InstanceData reconciliation conflict. InstanceId={InstanceId} ExpectedDataId={ExpectedDataId} ObservedDataId={ObservedDataId} Attempt={Attempt} ContributionCount={ContributionCount} PipelineStep={PipelineStep} TransitionKey={TransitionKey}")]
public static partial void InstanceDataReconciliationConflict(
    this ILogger logger, Guid instanceId, Guid expectedDataId, Guid? observedDataId,
    int attempt, int contributionCount, string pipelineStep, string transitionKey);

[LoggerMessage(EventId = 10111, Level = LogLevel.Error,
    Message = "InstanceData reconciliation exhausted. InstanceId={InstanceId} Attempts={Attempts} ContributionCount={ContributionCount} PipelineStep={PipelineStep} TransitionKey={TransitionKey}")]
public static partial void InstanceDataReconciliationExhausted(
    this ILogger logger, Guid instanceId, int attempts, int contributionCount,
    string pipelineStep, string transitionKey);
```

Resolve `PipelineStep` and `TransitionKey` without changing the approved reconciliation interface:

```csharp
var pipelineStep = Activity.Current?.GetTagItem("workflow.pipeline.step")?.ToString() ?? "unknown";
var transitionKey = Activity.Current?.GetTagItem("workflow.transition.key")?.ToString() ?? "unknown";
```

On each `ConditionalAppendStatus.Conflict`, log `appendResult.ObservedHead?.DataId`, increment the local conflict count, and emit the aggregate metric when the operation succeeds, fails, or exhausts. Keep IDs/ETags in log fields only and never pass `JsonData` or serialized payload text.

- [ ] **Step 6: Add explicit default-off configuration**

Under the existing `WorkflowExecution` object in `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json`, add:

```json
"EnableInstanceDataReconciliation": false
```

- [ ] **Step 7: Run registrations, monitoring tests, and build**

Run: `./scripts/setup-netstandard-ref.sh`

Run: `dotnet build BBT.Workflow.slnx --no-restore`

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter FullyQualifiedName~PipelineServiceCollectionExtensionsTests`

Run: `dotnet test test/BBT.Workflow.Infrastructure.Tests --filter FullyQualifiedName~PrometheusWorkflowMetricsTests`

Expected: build and focused tests PASS; metric registration has no duplicate-name exception.

- [ ] **Step 8: Commit operability and rollout wiring**

```bash
git add src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/PipelineServiceCollectionExtensions.cs src/BBT.Workflow.Domain/Monitoring/IWorkflowMetrics.cs src/BBT.Workflow.Infrastructure/Monitoring/WorkflowMetrics.cs src/BBT.Workflow.Infrastructure/Monitoring/PrometheusWorkflowMetrics.cs src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs src/BBT.Workflow.Application/Execution/Transitions/Services/InstanceDataReconciliationService.cs orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json test/BBT.Workflow.Infrastructure.Tests/Monitoring/PrometheusWorkflowMetricsTests.cs test/BBT.Workflow.Application.Tests/Microsoft/Extensions/DependencyInjection/PipelineServiceCollectionExtensionsTests.cs
git commit -m "feat: instrument instance data reconciliation"
```

---

### Task 8: Reproduce the reported stale-snapshot race end to end

**Files:**
- Create: `test/BBT.Workflow.Application.Tests/Execution/Transitions/InstanceDataConcurrencyRegressionTests.cs`
- Modify: `test/BBT.Workflow.Infrastructure.Tests/Domains/Instances/InstanceDataConditionalAppendFunctionTests.cs`

**Interfaces:**
- Consumes: complete tracked snapshot, reconciliation service, atomic repository, and applicator stack.
- Produces: regression evidence that the legacy call throws while enabled reconciliation merges without pipeline/task replay.

- [ ] **Step 1: Add the exact control-case and fixed-path regression**

```csharp
[Fact]
public async Task Latest_only_stale_snapshot_should_rebase_original_input_without_restarting_pipeline()
{
    var live = CreateLatestOnlyInstance("2.0.0", "{\"base\":1}");
    var script = await BuildTrackedContextAsync(live);
    script.Instance!.AddData(Guid.NewGuid(), new JsonData("{\"local\":2}"), VersionStrategy.IncreasePatch);

    AdvanceLiveAndDatabaseHead(live, "3.0.0", "{\"base\":1,\"remote\":3}");

    Should.Throw<InvalidOperationException>(() =>
        LegacyReplayUnknownRows(live, script.Instance));

    var result = await _applicator.ApplyAsync(_transitionContext, script, CancellationToken.None);

    result.IsSuccess.ShouldBeTrue();
    _transitionContext.Data.Json.ShouldBe("{\"base\":1,\"remote\":3,\"local\":2}");
    _taskExecutionCount.ShouldBe(1);
    _pipelineExecutionCount.ShouldBe(1);
    _repositoryFreshHeadReadCount.ShouldBe(1);
    _repositoryConditionalAppendCount.ShouldBe(2);
}
```

- [ ] **Step 2: Add PostgreSQL concurrent-writer merge assertions**

Add real DB tests that start two transactions from one baseline and assert:

```csharp
latest.Data.Json.ShouldBe("{\"base\":1,\"writerA\":true,\"writerB\":true}");
rows.Count(x => x.IsLatest).ShouldBe(1);
rows.Select(x => x.VersionNo).ShouldBe(rows.Select(x => x.VersionNo).Order());
```

Add same-path scalar writers and assert the writer whose atomic append occurs last wins. Add array writers and assert the complete array from the last append replaces the prior array; do not add element-level merge expectations.

- [ ] **Step 3: Run the reported regression and concurrency matrix**

Run: `dotnet test test/BBT.Workflow.Application.Tests --filter FullyQualifiedName~InstanceDataConcurrencyRegressionTests`

Run: `dotnet test test/BBT.Workflow.Infrastructure.Tests --filter FullyQualifiedName~InstanceDataConditionalAppendFunctionTests`

Expected: PASS; legacy control throws the original latest-only older-line error, enabled path succeeds after one fresh-head read, and execution counters remain one.

- [ ] **Step 4: Commit regression coverage**

```bash
git add test/BBT.Workflow.Application.Tests/Execution/Transitions/InstanceDataConcurrencyRegressionTests.cs test/BBT.Workflow.Infrastructure.Tests/Domains/Instances/InstanceDataConditionalAppendFunctionTests.cs
git commit -m "test: cover stale instance data reconciliation"
```

---

### Task 9: Verify release readiness and document canary activation

**Files:**
- Modify: `docs/superpowers/specs/2026-07-23-instance-data-optimistic-reconciliation-design.md`
- Test: all affected test projects.

**Interfaces:**
- Consumes: Tasks 1-8.
- Produces: verified implementation, exact test evidence, and a canary/rollback note tied to the existing feature flag.

- [ ] **Step 1: Run formatting without changing unrelated files**

Run: `dotnet format BBT.Workflow.slnx --no-restore --include src/BBT.Workflow.Domain/Instances src/BBT.Workflow.Domain/Scripting src/BBT.Workflow.Domain/Execution/Transitions/Context src/BBT.Workflow.Application/Execution/Transitions src/BBT.Workflow.Application/BackgroundJobs/Options src/BBT.Workflow.Infrastructure/Instances src/BBT.Workflow.Infrastructure/Monitoring test/BBT.Workflow.Domain.Tests/Instances test/BBT.Workflow.Domain.Tests/Scripting test/BBT.Workflow.Application.Tests/Execution/Transitions test/BBT.Workflow.Infrastructure.Tests/Domains/Instances`

Expected: exit code 0; inspect `git diff --stat` and revert no unrelated user changes.

- [ ] **Step 2: Run the targeted domain and application suites**

Run: `dotnet test test/BBT.Workflow.Domain.Tests --no-restore --filter "FullyQualifiedName~InstanceData|FullyQualifiedName~ScriptContext"`

Run: `dotnet test test/BBT.Workflow.Application.Tests --no-restore --filter "FullyQualifiedName~InstanceData|FullyQualifiedName~TaskStepDataReconciliation|FullyQualifiedName~ScriptDataChangeApplicator"`

Expected: PASS with zero failed tests.

- [ ] **Step 3: Run the real PostgreSQL suite and solution build**

Run: `dotnet test test/BBT.Workflow.Infrastructure.Tests --no-restore --filter "FullyQualifiedName~InstanceDataConditionalAppendFunctionTests|FullyQualifiedName~EfCoreInstanceDataConcurrencyRepositoryTests|FullyQualifiedName~InstanceDataVersioningTests"`

Run: `dotnet build BBT.Workflow.slnx --no-restore`

Expected: PASS; record exact passed/failed/skipped counts and build warnings.

- [ ] **Step 4: Add rollout evidence to the approved design document**

Append a `## Implementation Verification` section containing the exact commit SHA, commands/counts from Steps 2-3, and this activation sequence:

```text
1. Apply migration 20260723120000 to every flow schema.
2. Deploy application with WorkflowExecution:EnableInstanceDataReconciliation=false.
3. Enable only for canary orchestration deployments/flows handling parallel notifications.
4. Watch fast-path ratio, conflicts, attempts, exhausted count, duration, and task execution counters.
5. Roll back callers by setting the flag false; do not drop the database function during rollback.
```

- [ ] **Step 5: Inspect final scope and commit verification documentation**

Run: `git status --short`

Run: `git diff --check`

Expected: no whitespace errors; `.superpowers/` remains untracked and is not staged.

```bash
git add docs/superpowers/specs/2026-07-23-instance-data-optimistic-reconciliation-design.md
git commit -m "docs: record reconciliation verification"
```
