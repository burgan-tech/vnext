using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.PostCommit;

public sealed class PostCommitParentMutationServiceTests
{
    private const string Domain = "test-domain";
    private const string WorkflowKey = "test-workflow";
    private const string WorkflowVersion = "1.0.0";

    [Fact]
    public void PostCommitParentSnapshot_ClonesRequestDataAndNeverCarriesTrackedInstance()
    {
        var headers = new Dictionary<string, string?> { ["userId"] = "42" };
        var routes = new Dictionary<string, string?> { ["route"] = "original" };

        var snapshot = new PostCommitParentSnapshot(
            Domain,
            WorkflowKey,
            WorkflowVersion,
            Guid.NewGuid(),
            "transition",
            ExecMode.Sync,
            "trace",
            headers,
            routes,
            null,
            CreateWorkflow(State.Create("state", StateType.Intermediate, StateSubType.None, VersionStrategy.IncreaseMinor.Code)));
        headers["userId"] = "mutated";
        routes["route"] = "mutated";

        snapshot.Headers["userId"].ShouldBe("42");
        snapshot.RouteValues["route"].ShouldBe("original");
        typeof(PostCommitParentSnapshot).GetProperties()
            .ShouldNotContain(property => property.PropertyType == typeof(Instance));
    }

    [Fact]
    public async Task SettleAsync_AcquiresParentLockBeforeFreshUowReloadAndMutatesOnlyAuthoritativeInstance()
    {
        var calls = new List<string>();
        var sourceInstance = CreateBusyInstance();
        var authoritative = CreateBusyInstance(sourceInstance.Id);
        var fixture = CreateFixture(authoritative, calls);

        var result = await fixture.Service.SettleAsync(
            CreateSnapshot(sourceInstance.Id),
            CreateContinuations(resolvedStatus: InstanceStatus.Active),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.PipelineInstance.ShouldBeSameAs(authoritative);
        result.Value.Status.ShouldBe(InstanceStatus.Active);
        authoritative.Status.ShouldBe(InstanceStatus.Active);
        sourceInstance.Status.ShouldBe(InstanceStatus.Busy);
        calls.ShouldBe(["lock", "uow", "reload", "settle", "commit", "unlock"]);
        await fixture.StatusLock.Received(1).AcquireAsync(
            $"vnext:{Domain}:{WorkflowKey}:{authoritative.Id}",
            Arg.Any<CancellationToken>());
        fixture.UowManager.Received(1).Begin(
            Arg.Is<UnitOfWorkOptions>(options => options.Scope == UnitOfWorkScopeOption.RequiresNew));
    }

    [Fact]
    public async Task SettleAsync_WhenLockCannotBeAcquired_ReturnsConflictWithoutStartingUowOrReloading()
    {
        var calls = new List<string>();
        var authoritative = CreateBusyInstance();
        var fixture = CreateFixture(authoritative, calls, lockAcquired: false);

        var result = await fixture.Service.SettleAsync(
            CreateSnapshot(authoritative.Id),
            CreateContinuations(resolvedStatus: InstanceStatus.Active),
            CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrors.InstanceLockConflict(authoritative.Id).Code);
        calls.ShouldBe(["lock", "unlock"]);
        await fixture.Repository.DidNotReceiveWithAnyArgs()
            .FindWithAllCorrelationsAndDataAsync(default, default);
        fixture.UowManager.DidNotReceiveWithAnyArgs().Begin(default!);
    }

    [Fact]
    public async Task SettleAsync_WhenCallbackCompletedParent_DoesNotOverwriteAuthoritativeTerminalState()
    {
        var sourceInstance = CreateBusyInstance();
        var authoritative = Instance.Create(sourceInstance.Id, WorkflowKey, WorkflowVersion, "authoritative");
        authoritative.Complete(Domain, sync: true);
        var fixture = CreateFixture(authoritative);

        var result = await fixture.Service.SettleAsync(
            CreateSnapshot(sourceInstance.Id),
            CreateContinuations(resolvedStatus: InstanceStatus.Active),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Status.ShouldBe(InstanceStatus.Completed);
        result.Value.PipelineInstance.ShouldBeSameAs(authoritative);
        authoritative.Status.ShouldBe(InstanceStatus.Completed);
        sourceInstance.Status.ShouldBe(InstanceStatus.Busy);
        await fixture.Repository.DidNotReceiveWithAnyArgs()
            .UpdateAsync(default!, default, default);
        await fixture.NotificationScheduler.DidNotReceiveWithAnyArgs()
            .ScheduleAsync(default!, default);
    }

    [Fact]
    public async Task SettleAsync_ReloadsCallbackCorrelationAndDoesNotResolveParentWhileBlockingChildIsActive()
    {
        var sourceInstance = CreateBusyInstance();
        var authoritative = CreateBusyInstance(sourceInstance.Id);
        authoritative.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(), authoritative.Id, "waiting-child", Guid.NewGuid(),
            SubFlowType.SubFlow.Code, "child-domain", "child-flow", WorkflowVersion));
        var fixture = CreateFixture(authoritative);

        var result = await fixture.Service.SettleAsync(
            CreateSnapshot(sourceInstance.Id),
            CreateContinuations(resolvedStatus: InstanceStatus.Active),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Status.ShouldBe(InstanceStatus.Busy);
        authoritative.Status.ShouldBe(InstanceStatus.Busy);
        sourceInstance.ActiveCorrelations.ShouldBeEmpty();
        await fixture.Repository.DidNotReceiveWithAnyArgs()
            .UpdateAsync(default!, default, default);
    }

    [Fact]
    public async Task SettleAsync_WhenCallbackAlreadySettledActiveNotifyingState_DoesNotScheduleNotificationAgain()
    {
        var authoritative = CreateBusyInstance();
        authoritative.ChangeState(CreateNotifyingState("callback-settled", StateSubType.None));
        authoritative.Active();
        var workflow = CreateWorkflow(CreateNotifyingState("callback-settled", StateSubType.None));
        var fixture = CreateFixture(authoritative, workflow: workflow);

        var result = await fixture.Service.SettleAsync(
            CreateSnapshot(authoritative.Id, workflow),
            CreateContinuations(resolvedStatus: InstanceStatus.Active),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Status.ShouldBe(InstanceStatus.Active);
        await fixture.Repository.DidNotReceiveWithAnyArgs()
            .UpdateAsync(default!, default, default);
        await fixture.NotificationScheduler.DidNotReceiveWithAnyArgs()
            .ScheduleAsync(default!, default);
    }

    [Fact]
    public async Task SettleAsync_WhenFreshBusyParentResolvesToActive_SchedulesNotification()
    {
        var authoritative = CreateBusyInstance();
        authoritative.ChangeState(CreateNotifyingState("freshly-resolved", StateSubType.None));
        var workflow = CreateWorkflow(CreateNotifyingState("freshly-resolved", StateSubType.None));
        var fixture = CreateFixture(authoritative, workflow: workflow);

        var result = await fixture.Service.SettleAsync(
            CreateSnapshot(authoritative.Id, workflow),
            CreateContinuations(resolvedStatus: InstanceStatus.Active),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        authoritative.Status.ShouldBe(InstanceStatus.Active);
        await fixture.NotificationScheduler.Received(1).ScheduleAsync(
            Arg.Is<TransitionExecutionContext>(context =>
                ReferenceEquals(context.Instance, authoritative) &&
                context.Target != null &&
                context.Target.Key == "freshly-resolved"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SettleAsync_AsyncCaller_ReloadsWithoutLatestData()
    {
        // Nothing on the async path reads the parent's data: the settlement guard reads only
        // status + open correlations, and the job handler discards the output's PipelineInstance.
        var authoritative = CreateBusyInstance();
        var fixture = CreateFixture(authoritative);

        var result = await fixture.Service.SettleAsync(
            CreateSnapshot(authoritative.Id, callerMode: ExecMode.Async),
            CreateContinuations(resolvedStatus: InstanceStatus.Active),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        authoritative.Status.ShouldBe(InstanceStatus.Active);
        await fixture.Repository.Received(1).FindForPostCommitSettlementAsync(
            authoritative.Id, includeLatestData: false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SettleAsync_SyncCaller_ReloadsWithLatestData()
    {
        // A sync caller projects the settled PipelineInstance into the response (attributes,
        // entity ETag), so the latest data row must ride along with the reload.
        var authoritative = CreateBusyInstance();
        var fixture = CreateFixture(authoritative);

        var result = await fixture.Service.SettleAsync(
            CreateSnapshot(authoritative.Id, callerMode: ExecMode.Sync),
            CreateContinuations(resolvedStatus: InstanceStatus.Active),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await fixture.Repository.Received(1).FindForPostCommitSettlementAsync(
            authoritative.Id, includeLatestData: true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ExecMode.Sync)]
    [InlineData(ExecMode.Async)]
    public async Task FaultAsync_ReloadsWithLatestDataRegardlessOfCallerMode(ExecMode callerMode)
    {
        // Instance.Fault publishes the faulted SubFlow's latest data upward to its parent
        // (InstanceSubFaultedEvent.InstanceData); whether the instance is a SubFlow is only known
        // after the reload, and faults are the exceptional path — so the fault reload always
        // carries the data rather than paying a second query to find out.
        var authoritative = CreateBusyInstance();
        var fixture = CreateFixture(authoritative);

        var result = await fixture.Service.FaultAsync(
            CreateSnapshot(authoritative.Id, callerMode: callerMode),
            new PostCommitFaultRequest("PostCommit:Dependency", "child invocation failed", "stack"),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await fixture.Repository.Received(1).FindForPostCommitSettlementAsync(
            authoritative.Id, includeLatestData: true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FaultAsync_FaultsOnlyFreshAuthoritativeInstanceAndPersistsGeneratedEvents()
    {
        var sourceInstance = CreateBusyInstance();
        var authoritative = CreateBusyInstance(sourceInstance.Id);
        var fixture = CreateFixture(authoritative);
        var request = new PostCommitFaultRequest("PostCommit:Dependency", "child invocation failed", "stack");

        var result = await fixture.Service.FaultAsync(
            CreateSnapshot(sourceInstance.Id),
            request,
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Status.ShouldBe(InstanceStatus.Faulted);
        result.Value.PipelineInstance.ShouldBeSameAs(authoritative);
        authoritative.Status.ShouldBe(InstanceStatus.Faulted);
        authoritative.GetIncidentsForMonitor().Single().ErrorCode.ShouldBe(request.ErrorCode);
        authoritative.GetDomainEvents().ShouldNotBeEmpty();
        sourceInstance.Status.ShouldBe(InstanceStatus.Busy);
        sourceInstance.GetIncidentsForMonitor().ShouldBeEmpty();
        await fixture.Repository.Received(1).UpdateAsync(
            authoritative,
            true,
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("faulted")]
    public async Task FaultAsync_WhenCallbackAlreadyMadeParentTerminal_DoesNotOverwriteOrAddIncident(string terminalState)
    {
        var authoritative = Instance.Create(Guid.NewGuid(), WorkflowKey, WorkflowVersion, "authoritative");
        if (terminalState == "completed")
            authoritative.Complete(Domain, sync: true);
        else
            authoritative.Fault(Domain, sync: true);
        authoritative.ClearDomainEvents();
        var expectedStatus = authoritative.Status;
        var existingIncidentCount = authoritative.GetIncidentsForMonitor().Count;
        var fixture = CreateFixture(authoritative);

        var result = await fixture.Service.FaultAsync(
            CreateSnapshot(authoritative.Id),
            new PostCommitFaultRequest("PostCommit:LateFailure", "late failure"),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Status.ShouldBe(expectedStatus);
        authoritative.Status.ShouldBe(expectedStatus);
        authoritative.GetIncidentsForMonitor().Count.ShouldBe(existingIncidentCount);
        await fixture.Repository.DidNotReceiveWithAnyArgs()
            .UpdateAsync(default!, default, default);
    }

    private static Fixture CreateFixture(
        Instance authoritative,
        List<string>? calls = null,
        bool lockAcquired = true,
        Definitions.Workflow? workflow = null)
    {
        calls ??= [];
        workflow ??= CreateWorkflow(State.Create("state", StateType.Intermediate, StateSubType.None, VersionStrategy.IncreaseMinor.Code));

        var lockScope = Substitute.For<ITransitionLockScope>();
        lockScope.IsAcquired.Returns(lockAcquired);
        lockScope.LockKey.Returns($"vnext:{Domain}:{WorkflowKey}:{authoritative.Id}");
        lockScope.DisposeAsync().Returns(_ =>
        {
            calls.Add("unlock");
            return ValueTask.CompletedTask;
        });

        var statusLock = Substitute.For<BBT.Workflow.Execution.Pipeline.IInstanceStatusLock>();
        statusLock.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("lock");
                return Task.FromResult(lockScope);
            });

        var uow = Substitute.For<IUnitOfWork>();
        uow.CommitAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("commit");
            return Task.CompletedTask;
        });
        var uowManager = Substitute.For<IUnitOfWorkManager>();
        uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(_ =>
        {
            calls.Add("uow");
            return uow;
        });

        var repository = Substitute.For<IInstanceRepository>();
        repository.FindForPostCommitSettlementAsync(authoritative.Id, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("reload");
                return Task.FromResult<Instance?>(authoritative);
            });
        repository.UpdateAsync(authoritative, true, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("update");
                return Task.FromResult(authoritative);
            });
        // The settlement flip is an aggregate-aware CAS now: on success the repository applies
        // Active() in memory and aligns the tracker baseline — the mock mimics that contract.
        repository.TryReleaseBusyAsync(authoritative, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("settle");
                authoritative.Active();
                return Task.FromResult(true);
            });

        var notificationScheduler = Substitute.For<IStateNotificationScheduler>();

        var service = new PostCommitParentMutationService(
            uowManager,
            repository,
            statusLock,
            notificationScheduler,
            NullLogger<PostCommitParentMutationService>.Instance);

        return new Fixture(service, repository, uowManager, statusLock, notificationScheduler);
    }

    private static Instance CreateBusyInstance(Guid? id = null)
    {
        var instance = Instance.Create(id ?? Guid.NewGuid(), WorkflowKey, WorkflowVersion, "instance-key");
        instance.Busy();
        return instance;
    }

    private static PostCommitParentSnapshot CreateSnapshot(
        Guid instanceId,
        Definitions.Workflow? workflow = null,
        ExecMode callerMode = ExecMode.Sync) => new(
        Domain,
        WorkflowKey,
        WorkflowVersion,
        instanceId,
        "source-transition",
        callerMode,
        "trace-id",
        new Dictionary<string, string?> { ["userId"] = "42" },
        new Dictionary<string, string?> { ["route"] = "value" },
        null,
        workflow ?? CreateWorkflow(
            State.Create("state", StateType.Intermediate, StateSubType.None, VersionStrategy.IncreaseMinor.Code)));

    private static ContinuationSet CreateContinuations(
        InstanceStatus? resolvedStatus = null) => new(
        null,
        Array.Empty<IPostCommitJob>(),
        resolvedStatus,
        null,
        false,
        EpilogueMode.Run);

    private static Definitions.Workflow CreateWorkflow(State state)
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(WorkflowKey, Domain, "sys-flows", WorkflowVersion));
        workflow.AddState(state);
        return workflow;
    }

    private static State CreateNotifyingState(string key, StateSubType subType = StateSubType.Busy)
    {
        var json = $$"""
        {
            "key": "{{key}}",
            "stateType": "Intermediate",
            "subType": "{{subType}}",
            "versionStrategy": "Patch",
            "notifications": [
                { "type": "state", "mapping": { "code": "Y29kZQ==", "encoding": "Base64" } }
            ]
        }
        """;

        return System.Text.Json.JsonSerializer.Deserialize<State>(json, JsonSerializerConstants.JsonOptions)!;
    }

    private sealed record Fixture(
        PostCommitParentMutationService Service,
        IInstanceRepository Repository,
        IUnitOfWorkManager UowManager,
        BBT.Workflow.Execution.Pipeline.IInstanceStatusLock StatusLock,
        IStateNotificationScheduler NotificationScheduler);
}
