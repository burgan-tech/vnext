# Waiting Background Job Cancellation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Ensure completion, fault, and cancel-transition cleanup atomically cancels only waiting background jobs while running jobs retain their dispatcher claim and produce truthful terminal logs.

**Architecture:** Aether owns the concurrency invariant through a conditional EF Core update over Pending, Scheduled, and Retrying. Its service classifies outcomes and deletes scheduler entries only after commit. vNext consumes the result to decide whether an InstanceJob is processed or left active for its running handler, while JobDispatcher logs success only when its terminal CAS succeeds.

**Tech Stack:** .NET 10, C#, EF Core 10, PostgreSQL, xUnit, Shouldly, NSubstitute, Moq, Aether Unit of Work, Dapr Jobs abstraction.

## Global Constraints

- Cleanup-cancellable statuses are exactly Pending, Scheduled, and Retrying.
- Cleanup must never change a Running job or delete its scheduler entry.
- Eligibility and cancellation must be one atomic database update; no read-then-unconditional-delete implementation.
- Completion, fault, state-exit, and user cancel-transition cleanup share one policy.
- Existing unconditional IBackgroundJobService.DeleteAsync remains unchanged.
- No cooperative cancellation, in-flight callback interruption, or database migration.
- Preserve unrelated changes: Aether PostgreSqlRelationName.cs and AGENTS.md, and vNext appsettings.json must not be staged.
- Use targeted tests first and PostgreSQL tests with -m:1.

## File Map

Aether repository: /Users/U0B006/Documents/repos/burgan-tech/aether

- Create framework/src/BBT.Aether.Core/BBT/Aether/BackgroundJob/BackgroundJobCancellationResult.cs.
- Modify IBackgroundJobService.cs, IJobStore.cs, EfCoreJobStore.cs, BackgroundJobService.cs, and JobDispatcher.cs.
- Add service tests; extend JobStoreClaimReaperTests.cs and JobDispatcherTests.cs.

vNext repository: /Users/U0B006/Documents/repos/burgan-tech/vnext

- Modify InstanceCancellationService.cs and WorkflowLogs.cs.
- Extend InstanceCancellationServiceTests.cs.

---

### Task 1: Atomic Waiting-State Store Transition

**Files:**
- Modify: framework/src/BBT.Aether.Domain/BBT/Aether/Domain/Repositories/IJobStore.cs
- Modify: framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/EfCoreJobStore.cs
- Modify: framework/test/BBT.Aether.Postgres.Tests/BackgroundJob/JobStoreClaimReaperTests.cs

**Interfaces:**
- Consumes: BackgroundJobStatus, BackgroundJobInfo, TryClaimAsync, ExecuteUpdateAsync.
- Produces: Task<bool> TryCancelWaitingAsync(Guid id, DateTime handledTimeUtc, CancellationToken cancellationToken = default).

- [ ] **Step 1: Write failing eligible-state PostgreSQL tests**

Add to JobStoreClaimReaperTests using its existing setup helpers:

~~~csharp
[Theory]
[InlineData(BackgroundJobStatus.Pending)]
[InlineData(BackgroundJobStatus.Scheduled)]
[InlineData(BackgroundJobStatus.Retrying)]
public async Task TryCancelWaiting_cancels_waiting_status(BackgroundJobStatus initial)
{
    var sp = BuildProvider();
    await ArrangeSchemaAsync(sp);
    var id = Guid.NewGuid();
    var handledAt = DateTime.UtcNow;
    await SeedAsync(sp, NewJob(id, initial));

    await RunInUowAsync(sp, async store =>
        (await store.TryCancelWaitingAsync(id, handledAt)).ShouldBeTrue());

    var job = await ReloadAsync(sp, id);
    job!.Status.ShouldBe(BackgroundJobStatus.Cancelled);
    job.HandledTime.ShouldBe(handledAt, TimeSpan.FromSeconds(1));
    job.RunningSince.ShouldBeNull();
    job.RunningToken.ShouldBeNull();
    job.ArmingToken.ShouldBeNull();
    job.ArmingUntil.ShouldBeNull();
}
~~~

- [ ] **Step 2: Write failing Running and terminal protection tests**

~~~csharp
[Fact]
public async Task TryCancelWaiting_preserves_running_claim()
{
    var sp = BuildProvider();
    await ArrangeSchemaAsync(sp);
    var id = Guid.NewGuid();
    var token = Guid.NewGuid();
    await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Scheduled));

    await RunInUowAsync(sp, async store =>
    {
        (await store.TryClaimAsync(id, DateTime.UtcNow, token)).ShouldBeTrue();
        (await store.TryCancelWaitingAsync(id, DateTime.UtcNow)).ShouldBeFalse();
    });

    var job = await ReloadAsync(sp, id);
    job!.Status.ShouldBe(BackgroundJobStatus.Running);
    job.RunningToken.ShouldBe(token);
    job.RunningSince.ShouldNotBeNull();
}

[Theory]
[InlineData(BackgroundJobStatus.Completed)]
[InlineData(BackgroundJobStatus.Failed)]
[InlineData(BackgroundJobStatus.Cancelled)]
public async Task TryCancelWaiting_preserves_terminal_status(BackgroundJobStatus initial)
{
    var sp = BuildProvider();
    await ArrangeSchemaAsync(sp);
    var id = Guid.NewGuid();
    await SeedAsync(sp, NewJob(id, initial));

    await RunInUowAsync(sp, async store =>
        (await store.TryCancelWaitingAsync(id, DateTime.UtcNow)).ShouldBeFalse());

    (await ReloadAsync(sp, id))!.Status.ShouldBe(initial);
}
~~~

- [ ] **Step 3: Run red tests**

~~~bash
dotnet test framework/test/BBT.Aether.Postgres.Tests/BBT.Aether.Postgres.Tests.csproj \
  --no-restore -m:1 --filter "FullyQualifiedName~TryCancelWaiting"
~~~

Expected: compilation fails because TryCancelWaitingAsync does not exist.

- [ ] **Step 4: Add the store contract**

Add after TryClaimAsync in IJobStore:

~~~csharp
/// <summary>
/// Atomically cancels a Pending, Scheduled, or Retrying job.
/// Running and terminal jobs are not changed.
/// </summary>
Task<bool> TryCancelWaitingAsync(
    Guid id,
    DateTime handledTimeUtc,
    CancellationToken cancellationToken = default);
~~~

- [ ] **Step 5: Implement the single conditional update**

Add beside TryClaimAsync in EfCoreJobStore:

~~~csharp
public async Task<bool> TryCancelWaitingAsync(
    Guid id,
    DateTime handledTimeUtc,
    CancellationToken cancellationToken = default)
{
    if (id == Guid.Empty)
        throw new ArgumentException("Id cannot be empty.", nameof(id));

    using var schemaScope = BeginConfiguredSchemaScope();
    var dbContext = await _dbContextProvider.GetDbContextAsync(cancellationToken);
    var now = DateTime.UtcNow;

    var affected = await dbContext.BackgroundJobs
        .Where(j => j.Id == id &&
                    (j.Status == BackgroundJobStatus.Pending ||
                     j.Status == BackgroundJobStatus.Scheduled ||
                     j.Status == BackgroundJobStatus.Retrying))
        .ExecuteUpdateAsync(setters => setters
            .SetProperty(j => j.Status, BackgroundJobStatus.Cancelled)
            .SetProperty(j => j.HandledTime, handledTimeUtc)
            .SetProperty(j => j.RunningSince, (DateTime?)null)
            .SetProperty(j => j.RunningToken, (Guid?)null)
            .SetProperty(j => j.ArmingToken, (Guid?)null)
            .SetProperty(j => j.ArmingUntil, (DateTime?)null)
            .SetProperty(j => j.ModifiedAt, now), cancellationToken);

    return affected > 0;
}
~~~

- [ ] **Step 6: Run state tests and verify green**

Use the Step 3 command. Expected: all TryCancelWaiting tests pass.

- [ ] **Step 7: Add the real PostgreSQL claim-vs-cancel race**

~~~csharp
[Fact]
public async Task Claim_and_waiting_cancellation_have_exactly_one_winner()
{
    var sp = BuildProvider();
    await ArrangeSchemaAsync(sp);
    var id = Guid.NewGuid();
    await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Scheduled));

    var results = await Task.WhenAll(
        RunInNewUowAsync(sp, store =>
            store.TryClaimAsync(id, DateTime.UtcNow, Guid.NewGuid())),
        RunInNewUowAsync(sp, store =>
            store.TryCancelWaitingAsync(id, DateTime.UtcNow)));

    results.Count(won => won).ShouldBe(1);
    var status = (await ReloadAsync(sp, id))!.Status;
    new[] { BackgroundJobStatus.Running, BackgroundJobStatus.Cancelled }
        .ShouldContain(status);
}
~~~

Add the value-returning helper:

~~~csharp
private async Task<T> RunInNewUowAsync<T>(
    IServiceProvider sp,
    Func<IJobStore, Task<T>> action)
{
    await using var scope = sp.CreateAsyncScope();
    var services = scope.ServiceProvider;
    using var schema = services.GetRequiredService<ICurrentSchema>().Change(_schema);
    await using var uow = services.GetRequiredService<IUnitOfWorkManager>().Begin(
        new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = true
        });
    var result = await action(services.GetRequiredService<IJobStore>());
    await uow.CommitAsync();
    return result;
}
~~~

- [ ] **Step 8: Run the full store class**

~~~bash
dotnet test framework/test/BBT.Aether.Postgres.Tests/BBT.Aether.Postgres.Tests.csproj \
  --no-restore -m:1 --filter "FullyQualifiedName~JobStoreClaimReaperTests"
~~~

Expected: all tests pass and exactly one race contender wins.

- [ ] **Step 9: Commit Task 1 only**

~~~bash
git add framework/src/BBT.Aether.Domain/BBT/Aether/Domain/Repositories/IJobStore.cs
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/EfCoreJobStore.cs
git add framework/test/BBT.Aether.Postgres.Tests/BackgroundJob/JobStoreClaimReaperTests.cs
git commit -m "feat(background-jobs): add atomic waiting cancellation"
~~~

---

### Task 2: Classified Aether Cancellation Service

**Files:**
- Create: framework/src/BBT.Aether.Core/BBT/Aether/BackgroundJob/BackgroundJobCancellationResult.cs
- Modify: framework/src/BBT.Aether.Core/BBT/Aether/BackgroundJob/IBackgroundJobService.cs
- Modify: framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/BackgroundJobService.cs
- Create: framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/BackgroundJob/BackgroundJobServiceCancelWaitingTests.cs

**Interfaces:**
- Consumes: TryCancelWaitingAsync from Task 1, IUnitOfWork.OnCompleted, IJobScheduler.DeleteAsync.
- Produces: BackgroundJobCancellationResult and CancelWaitingAsync for vNext.

- [ ] **Step 1: Create failing outcome-classification tests**

Start the new test class with this concrete fixture:

~~~csharp
public sealed class BackgroundJobServiceCancelWaitingTests
{
    private readonly IJobStore _jobStore = Substitute.For<IJobStore>();
    private readonly IJobScheduler _jobScheduler = Substitute.For<IJobScheduler>();
    private readonly IUnitOfWorkManager _uowManager = Substitute.For<IUnitOfWorkManager>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly BackgroundJobService _sut;

    public BackgroundJobServiceCancelWaitingTests()
    {
        _clock.UtcNow.Returns(DateTime.UtcNow);
        _sut = new BackgroundJobService(
            _jobStore,
            _jobScheduler,
            _uowManager,
            Substitute.For<IGuidGenerator>(),
            _clock,
            Substitute.For<ICurrentSchema>(),
            Substitute.For<IEventSerializer>(),
            new BackgroundJobOptions(),
            Substitute.For<ILogger<BackgroundJobService>>());
    }
}
~~~

Place the following tests and helper inside that class:

~~~csharp
[Fact]
public async Task Running_returns_skipped_and_never_deletes_scheduler()
{
    var id = Guid.NewGuid();
    var running = NewJob(id, BackgroundJobStatus.Running);
    _jobStore.GetAsync(id, Arg.Any<CancellationToken>()).Returns(running, running);
    _jobStore.TryCancelWaitingAsync(id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
        .Returns(false);

    var result = await _sut.CancelWaitingAsync(id);

    result.ShouldBe(BackgroundJobCancellationResult.SkippedRunning);
    await _jobScheduler.DidNotReceiveWithAnyArgs()
        .DeleteAsync(default!, default!, default);
}

[Theory]
[InlineData(BackgroundJobStatus.Completed)]
[InlineData(BackgroundJobStatus.Failed)]
[InlineData(BackgroundJobStatus.Cancelled)]
public async Task Terminal_returns_already_terminal(BackgroundJobStatus status)
{
    var id = Guid.NewGuid();
    var terminal = NewJob(id, status);
    _jobStore.GetAsync(id, Arg.Any<CancellationToken>()).Returns(terminal, terminal);
    _jobStore.TryCancelWaitingAsync(id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
        .Returns(false);

    (await _sut.CancelWaitingAsync(id))
        .ShouldBe(BackgroundJobCancellationResult.AlreadyTerminal);
}

[Fact]
public async Task Missing_returns_not_found_without_store_transition()
{
    var id = Guid.NewGuid();
    _jobStore.GetAsync(id, Arg.Any<CancellationToken>())
        .Returns((BackgroundJobInfo?)null);

    (await _sut.CancelWaitingAsync(id))
        .ShouldBe(BackgroundJobCancellationResult.NotFound);
    await _jobStore.DidNotReceiveWithAnyArgs()
        .TryCancelWaitingAsync(default, default, default);
}

private static BackgroundJobInfo NewJob(Guid id, BackgroundJobStatus status) =>
    new(id, "handler", "job-1") { Status = status };
~~~

Also cover the single bounded retry when another writer performs a waiting-to-waiting mutation:

~~~csharp
[Fact]
public async Task Waiting_classification_retries_atomic_cancel_once()
{
    var id = Guid.NewGuid();
    var pending = NewJob(id, BackgroundJobStatus.Pending);
    _jobStore.GetAsync(id, Arg.Any<CancellationToken>()).Returns(pending, pending);
    _jobStore.TryCancelWaitingAsync(id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
        .Returns(false, true);
    var ambient = Substitute.For<IUnitOfWork>();
    _uowManager.Current.Returns(ambient);
    ambient.OnCompleted(Arg.Any<Func<IUnitOfWork, Task>>())
        .Returns(Substitute.For<IDisposable>());

    (await _sut.CancelWaitingAsync(id))
        .ShouldBe(BackgroundJobCancellationResult.Cancelled);

    await _jobStore.Received(2).TryCancelWaitingAsync(
        id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
}
~~~

- [ ] **Step 2: Run red tests**

~~~bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj \
  --no-restore -m:1 --filter "FullyQualifiedName~BackgroundJobServiceCancelWaitingTests"
~~~

Expected: compilation fails because the result enum and service method do not exist.

- [ ] **Step 3: Add the public result and service contract**

Create BackgroundJobCancellationResult.cs:

~~~csharp
namespace BBT.Aether.BackgroundJob;

public enum BackgroundJobCancellationResult
{
    Cancelled,
    SkippedRunning,
    AlreadyTerminal,
    NotFound
}
~~~

Add to IBackgroundJobService before DeleteAsync:

~~~csharp
Task<BackgroundJobCancellationResult> CancelWaitingAsync(
    Guid id,
    CancellationToken cancellationToken = default);
~~~

- [ ] **Step 4: Implement atomic cancellation and classification**

Add to BackgroundJobService before DeleteAsync:

~~~csharp
public async Task<BackgroundJobCancellationResult> CancelWaitingAsync(
    Guid id,
    CancellationToken cancellationToken = default)
{
    if (id == Guid.Empty)
        throw new ArgumentException("Id cannot be empty.", nameof(id));

    var existing = await jobStore.GetAsync(id, cancellationToken);
    if (existing is null)
        return BackgroundJobCancellationResult.NotFound;

    if (uowManager.Current is { } ambient)
    {
        var result = await TryCancelAndClassifyAsync(id, cancellationToken);
        if (result == BackgroundJobCancellationResult.Cancelled)
        {
            ambient.OnCompleted(_ => TryDeleteSchedulerEntryAsync(
                existing.HandlerName, existing.JobName, CancellationToken.None));
        }
        return result;
    }

    BackgroundJobCancellationResult result;
    await using (var uow = uowManager.Begin(new UnitOfWorkOptions
                 {
                     Scope = UnitOfWorkScopeOption.RequiresNew,
                     IsTransactional = true
                 }))
    {
        result = await TryCancelAndClassifyAsync(id, cancellationToken);
        await uow.CommitAsync(cancellationToken);
    }

    if (result == BackgroundJobCancellationResult.Cancelled)
        await TryDeleteSchedulerEntryAsync(
            existing.HandlerName, existing.JobName, cancellationToken);

    return result;
}

private async Task<BackgroundJobCancellationResult> TryCancelAndClassifyAsync(
    Guid id,
    CancellationToken cancellationToken)
{
    for (var attempt = 0; attempt < 2; attempt++)
    {
        if (await jobStore.TryCancelWaitingAsync(id, clock.UtcNow, cancellationToken))
            return BackgroundJobCancellationResult.Cancelled;

        var current = await jobStore.GetAsync(id, cancellationToken);
        if (current is null)
            return BackgroundJobCancellationResult.NotFound;
        if (current.Status == BackgroundJobStatus.Running)
            return BackgroundJobCancellationResult.SkippedRunning;
        if (current.Status is BackgroundJobStatus.Completed
            or BackgroundJobStatus.Failed
            or BackgroundJobStatus.Cancelled)
            return BackgroundJobCancellationResult.AlreadyTerminal;
        // A concurrent waiting-to-waiting mutation won. Retry the atomic update once.
    }

    throw new InvalidOperationException(
        $"Unable to classify waiting cancellation for job '{id}' after one retry.");
}

private async Task TryDeleteSchedulerEntryAsync(
    string handlerName,
    string jobName,
    CancellationToken cancellationToken)
{
    try
    {
        await jobScheduler.DeleteAsync(handlerName, jobName, cancellationToken);
    }
    catch (Exception ex)
    {
        logger.LogError(ex,
            "Background job '{JobName}' was cancelled in persistence but could not be deleted from the scheduler",
            jobName);
    }
}
~~~

The initial read captures immutable scheduler identity only. TryCancelWaitingAsync is the eligibility authority. Do not call DeleteAsync.

- [ ] **Step 5: Add ambient deferral and non-ambient ordering tests**

~~~csharp
[Fact]
public async Task Ambient_cancellation_defers_scheduler_delete()
{
    var ambient = Substitute.For<IUnitOfWork>();
    _uowManager.Current.Returns(ambient);
    var id = Guid.NewGuid();
    _jobStore.GetAsync(id, Arg.Any<CancellationToken>())
        .Returns(NewJob(id, BackgroundJobStatus.Scheduled));
    _jobStore.TryCancelWaitingAsync(id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
        .Returns(true);
    Func<IUnitOfWork, Task>? callback = null;
    ambient.OnCompleted(Arg.Do<Func<IUnitOfWork, Task>>(value => callback = value))
        .Returns(Substitute.For<IDisposable>());

    (await _sut.CancelWaitingAsync(id))
        .ShouldBe(BackgroundJobCancellationResult.Cancelled);
    await _jobScheduler.DidNotReceiveWithAnyArgs()
        .DeleteAsync(default!, default!, default);

    await callback!(ambient);
    await _jobScheduler.Received(1)
        .DeleteAsync("handler", "job-1", Arg.Any<CancellationToken>());
}

[Fact]
public async Task Non_ambient_cancellation_commits_before_scheduler_delete()
{
    var ownUow = Substitute.For<IUnitOfWork>();
    _uowManager.Current.Returns((IUnitOfWork?)null);
    _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(ownUow);
    var id = Guid.NewGuid();
    _jobStore.GetAsync(id, Arg.Any<CancellationToken>())
        .Returns(NewJob(id, BackgroundJobStatus.Pending));
    _jobStore.TryCancelWaitingAsync(id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
        .Returns(true);

    (await _sut.CancelWaitingAsync(id))
        .ShouldBe(BackgroundJobCancellationResult.Cancelled);

    Received.InOrder(() =>
    {
        _jobStore.TryCancelWaitingAsync(
            id, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        ownUow.CommitAsync(Arg.Any<CancellationToken>());
        _jobScheduler.DeleteAsync(
            "handler", "job-1", Arg.Any<CancellationToken>());
    });
}
~~~

Also assert that an ambient rollback, represented by never invoking the captured OnCompleted callback, produces no scheduler call.

- [ ] **Step 6: Run all service tests**

~~~bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj \
  --no-restore -m:1 --filter "FullyQualifiedName~BackgroundJobService"
~~~

Expected: all enqueue, update/delete, and cancellation tests pass.

- [ ] **Step 7: Commit Task 2 only**

~~~bash
git add framework/src/BBT.Aether.Core/BBT/Aether/BackgroundJob/BackgroundJobCancellationResult.cs
git add framework/src/BBT.Aether.Core/BBT/Aether/BackgroundJob/IBackgroundJobService.cs
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/BackgroundJobService.cs
git add framework/test/BBT.Aether.Infrastructure.Tests/BBT/Aether/BackgroundJob/BackgroundJobServiceCancelWaitingTests.cs
git commit -m "feat(background-jobs): cancel waiting jobs after commit"
~~~

---

### Task 3: Truthful Dispatcher Success Logging

**Files:**
- Modify: framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/JobDispatcher.cs
- Modify: framework/test/BBT.Aether.Postgres.Tests/BackgroundJob/JobDispatcherTests.cs

**Interfaces:**
- Consumes: TryRecordTerminalAsync.
- Produces: Task<bool> RecordSuccessAsync and success logging gated on persisted outcome.

- [ ] **Step 1: Make the test handler able to invalidate its own claim**

Inject IServiceProvider into the existing nested TestHandler. Add LoseClaim and JobId static fields, reset them, and after normal invocation open a RequiresNew UoW and call UpdateStatusAsync(JobId, Cancelled) when LoseClaim is true. This intentionally simulates an external state writer and is test-only.

~~~csharp
if (LoseClaim)
{
    var manager = serviceProvider.GetRequiredService<IUnitOfWorkManager>();
    await using var uow = manager.Begin(new UnitOfWorkOptions
    {
        Scope = UnitOfWorkScopeOption.RequiresNew,
        IsTransactional = true
    });
    await serviceProvider.GetRequiredService<IJobStore>().UpdateStatusAsync(
        JobId,
        BackgroundJobStatus.Cancelled,
        DateTime.UtcNow,
        cancellationToken: cancellationToken);
    await uow.CommitAsync(cancellationToken);
}
~~~

- [ ] **Step 2: Write the failing logging test**

Make BuildDispatcher accept an optional ILogger<JobDispatcher>. Use an NSubstitute logger and a HasLog helper patterned after BackgroundJobServiceTransactionalEnqueueTests.

~~~csharp
private static bool HasLog(
    ILogger<JobDispatcher> logger,
    LogLevel level,
    string message) =>
    logger.ReceivedCalls().Any(call =>
    {
        var arguments = call.GetArguments();
        return arguments.Length >= 3
               && arguments[0] is LogLevel actualLevel
               && actualLevel == level
               && string.Equals(
                   arguments[2]?.ToString(), message, StringComparison.Ordinal);
    });
~~~

~~~csharp
[Fact]
public async Task Lost_success_claim_warns_without_success_log()
{
    TestHandler.Reset();
    var scheduler = new FakeJobScheduler();
    var options = BuildOptions();
    var sp = BuildProvider(scheduler, options);
    await ArrangeSchemaAsync(sp);
    var id = Guid.NewGuid();
    await SeedAsync(sp, NewJob(id, BackgroundJobStatus.Scheduled));
    TestHandler.JobId = id;
    TestHandler.LoseClaim = true;
    var logger = Substitute.For<ILogger<JobDispatcher>>();

    await BuildDispatcher(sp, options, logger)
        .DispatchAsync(JobNameFor(id), BuildPayload(sp));

    HasLog(logger, LogLevel.Warning,
        $"Claim for job id '{id}' was lost before success could be recorded; skipping")
        .ShouldBeTrue();
    HasLog(logger, LogLevel.Information,
        $"Successfully completed handler '{HandlerName}' for job id '{id}'")
        .ShouldBeFalse();
}
~~~

- [ ] **Step 3: Run red test**

~~~bash
dotnet test framework/test/BBT.Aether.Postgres.Tests/BBT.Aether.Postgres.Tests.csproj \
  --no-restore -m:1 --filter "FullyQualifiedName~Lost_success_claim_warns_without_success_log"
~~~

Expected: warning assertion passes and the no-success assertion fails.

- [ ] **Step 4: Return and consume the record result**

Change the call site:

~~~csharp
var recorded = await RecordSuccessAsync(
    scope, c, jobName, activity, cancellationToken);
if (recorded)
{
    logger.LogInformation(
        "Successfully completed handler '{HandlerName}' for job id '{JobId}'",
        c.HandlerName,
        c.JobId);
}
~~~

Change RecordSuccessAsync to Task<bool>. Return false after the existing claim-loss warning; return true after activity status and one-shot scheduler deletion.

- [ ] **Step 5: Run the full dispatcher class**

~~~bash
dotnet test framework/test/BBT.Aether.Postgres.Tests/BBT.Aether.Postgres.Tests.csproj \
  --no-restore -m:1 --filter "FullyQualifiedName~JobDispatcherTests"
~~~

Expected: new regression plus existing one-shot, recurring, retry, and failure tests pass.

- [ ] **Step 6: Commit Task 3 only**

~~~bash
git add framework/src/BBT.Aether.Infrastructure/BBT/Aether/BackgroundJob/JobDispatcher.cs
git add framework/test/BBT.Aether.Postgres.Tests/BackgroundJob/JobDispatcherTests.cs
git commit -m "fix(background-jobs): suppress success log after lost claim"
~~~

---

### Task 4: Outcome-Aware vNext Cleanup

**Files:**
- Modify: src/BBT.Workflow.Application/Instances/Managers/InstanceCancellationService.cs
- Modify: src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs
- Modify: test/BBT.Workflow.Application.Tests/Instances/InstanceCancellationServiceTests.cs

**Interfaces:**
- Consumes: CancelWaitingAsync and BackgroundJobCancellationResult.
- Produces: one cleanup policy for full-instance and state-transition cleanup.

- [ ] **Step 1: Convert existing matching tests to the new API**

Configure CancelWaitingAsync to return Cancelled, verify it is called once, and verify the InstanceJob becomes inactive. In the different-source-state test, verify CancelWaitingAsync is never called.

- [ ] **Step 2: Add failing Running and stale-tracking tests**

~~~csharp
[Fact]
public async Task ProcessCancellation_running_job_remains_active()
{
    var job = CreateInstanceJob();
    _instanceJobRepository
        .Setup(r => r.GetListActiveAsync(_instance.Id, It.IsAny<CancellationToken>()))
        .ReturnsAsync(new List<InstanceJob> { job });
    _backgroundJobService
        .Setup(s => s.CancelWaitingAsync(job.JobId, It.IsAny<CancellationToken>()))
        .ReturnsAsync(BackgroundJobCancellationResult.SkippedRunning);

    var result = await CreateService().ProcessCancellationAsync(_instance.Id);

    result.IsSuccess.ShouldBeTrue();
    job.IsActive.ShouldBeTrue();
    _instanceJobRepository.Verify(r => r.UpdateAsync(
        It.IsAny<InstanceJob>(), false, It.IsAny<CancellationToken>()), Times.Never);
}

[Theory]
[InlineData(BackgroundJobCancellationResult.Cancelled)]
[InlineData(BackgroundJobCancellationResult.AlreadyTerminal)]
[InlineData(BackgroundJobCancellationResult.NotFound)]
public async Task ProcessCancellation_non_running_outcomes_close_tracking(
    BackgroundJobCancellationResult outcome)
{
    var job = CreateInstanceJob();
    _instanceJobRepository
        .Setup(r => r.GetListActiveAsync(_instance.Id, It.IsAny<CancellationToken>()))
        .ReturnsAsync(new List<InstanceJob> { job });
    _backgroundJobService
        .Setup(s => s.CancelWaitingAsync(job.JobId, It.IsAny<CancellationToken>()))
        .ReturnsAsync(outcome);

    var result = await CreateService().ProcessCancellationAsync(_instance.Id);

    result.IsSuccess.ShouldBeTrue();
    job.IsActive.ShouldBeFalse();
    _instanceJobRepository.Verify(r => r.UpdateAsync(
        job, false, It.IsAny<CancellationToken>()), Times.Once);
}
~~~

Add this concrete helper:

~~~csharp
private InstanceJob CreateInstanceJob(string transition = "check") =>
    InstanceJob.Create(
        Guid.NewGuid(),
        JobName.ForScheduledTransition(_instance.Id, "state-a", transition),
        Guid.NewGuid(),
        "bank",
        "flow",
        _instance.Id);
~~~

- [ ] **Step 3: Run red vNext tests**

~~~bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --no-restore -m:1 --filter "FullyQualifiedName~InstanceCancellationServiceTests"
~~~

Expected: production still calls DeleteAsync and marks Running tracking processed.

- [ ] **Step 4: Add the information-level skip log**

Add to WorkflowLogs.cs. Event ID 40104 is currently unused:

~~~csharp
[LoggerMessage(
    EventId = 40104,
    Level = LogLevel.Information,
    Message = "Background job {JobId} is already running for instance {InstanceId}; cleanup left it to the dispatcher")]
public static partial void InstanceJobCleanupSkippedRunning(
    this ILogger logger,
    Guid jobId,
    Guid instanceId);
~~~

Verify with rg that EventId 40104 occurs exactly once after the edit.

- [ ] **Step 5: Centralize result handling and use it in both loops**

~~~csharp
private async Task ProcessJobCancellationAsync(
    InstanceJob job,
    Guid instanceId,
    CancellationToken cancellationToken)
{
    var outcome = await backgroundJobService.CancelWaitingAsync(
        job.JobId, cancellationToken);
    if (outcome == BackgroundJobCancellationResult.SkippedRunning)
    {
        logger.InstanceJobCleanupSkippedRunning(job.JobId, instanceId);
        return;
    }

    job.MarkAsProcessed();
    await instanceJobRepository.UpdateAsync(job, false, cancellationToken);
}
~~~

Replace both DeleteAsync/MarkAsProcessed/UpdateAsync blocks with:

~~~csharp
await ProcessJobCancellationAsync(job, instance.Id, cancellationToken);
~~~

Keep each existing per-job try/catch so one failure does not stop later jobs.

- [ ] **Step 6: Add a mixed-list regression**

~~~csharp
[Fact]
public async Task ProcessCancellation_mixed_jobs_closes_waiting_and_preserves_running()
{
    var waiting = CreateInstanceJob("waiting");
    var running = CreateInstanceJob("running");
    _instanceJobRepository
        .Setup(r => r.GetListActiveAsync(_instance.Id, It.IsAny<CancellationToken>()))
        .ReturnsAsync(new List<InstanceJob> { waiting, running });
    _backgroundJobService
        .Setup(s => s.CancelWaitingAsync(waiting.JobId, It.IsAny<CancellationToken>()))
        .ReturnsAsync(BackgroundJobCancellationResult.Cancelled);
    _backgroundJobService
        .Setup(s => s.CancelWaitingAsync(running.JobId, It.IsAny<CancellationToken>()))
        .ReturnsAsync(BackgroundJobCancellationResult.SkippedRunning);

    var result = await CreateService().ProcessCancellationAsync(_instance.Id);

    result.IsSuccess.ShouldBeTrue();
    waiting.IsActive.ShouldBeFalse();
    running.IsActive.ShouldBeTrue();
}
~~~

- [ ] **Step 7: Run targeted vNext tests**

Use the Step 3 command. Expected: all matching and outcome tests pass.

- [ ] **Step 8: Commit Task 4 only**

~~~bash
git add src/BBT.Workflow.Application/Instances/Managers/InstanceCancellationService.cs
git add src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs
git add test/BBT.Workflow.Application.Tests/Instances/InstanceCancellationServiceTests.cs
git commit -m "fix(workflow): preserve running jobs during cleanup"
~~~

---

### Task 5: Cross-Repository Verification and Live Regression

**Files:** No production changes expected.

- [ ] **Step 1: Run targeted Aether tests**

~~~bash
dotnet test framework/test/BBT.Aether.Infrastructure.Tests/BBT.Aether.Infrastructure.Tests.csproj \
  --no-restore -m:1 --filter "FullyQualifiedName~BackgroundJobService"
dotnet test framework/test/BBT.Aether.Postgres.Tests/BBT.Aether.Postgres.Tests.csproj \
  --no-restore -m:1 \
  --filter "FullyQualifiedName~JobStoreClaimReaperTests|FullyQualifiedName~JobDispatcherTests"
~~~

Expected: zero failures.

- [ ] **Step 2: Build Aether**

~~~bash
dotnet build framework/BBT.Aether.slnx --no-restore -m:1
~~~

Expected: success; record unrelated pre-existing warnings separately.

- [ ] **Step 3: Test and build vNext**

From the vNext repository:

~~~bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --no-restore -m:1 --filter "FullyQualifiedName~InstanceCancellationServiceTests"
dotnet build BBT.Workflow.slnx --no-restore -m:1
~~~

Expected: success. Local project references mean no package-version update is needed.

- [ ] **Step 4: Verify worktree hygiene**

Run git status --short, git diff --check, and git log -5 --oneline in both repositories. Confirm the three unrelated user files remain outside task commits.

- [ ] **Step 5: Execute one terminal transition locally**

Query the executing row afterward:

~~~sql
SELECT "Id", "HandlerName", "JobName", "Status",
       "RunningSince", "RunningToken", "HandledTime", "ModifiedAt"
FROM sys_queues."BackgroundJobs"
WHERE "HandlerName" IN ('flow.transition', 'flow.timeout', 'flow.transition.schedule')
ORDER BY "ModifiedAt" DESC
LIMIT 10;
~~~

Expected executing row: Status 2 (Completed), RunningSince null, RunningToken null. Other waiting jobs for the instance should be Status 4 (Cancelled).

Search OpenObserve by the executing JobId. Expect one Successfully completed handler entry, no claim-lost warning, and optionally an information log that cleanup left a Running job to the dispatcher.

- [ ] **Step 6: Review against the approved spec**

Confirm each requirement in docs/superpowers/specs/2026-07-18-waiting-background-job-cancellation-design.md has test or live evidence: atomic waiting cancellation, Running preservation, post-commit scheduler deletion, active tracking for SkippedRunning, truthful dispatcher logs, and unchanged unconditional DeleteAsync.
