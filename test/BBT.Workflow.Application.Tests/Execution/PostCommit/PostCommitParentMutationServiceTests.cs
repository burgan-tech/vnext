using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Execution.PostCommit.Relay;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.SubFlow.Headers;
using Microsoft.Extensions.Options;
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
        authoritative.GetLoadedIncidents().Single().ErrorCode.ShouldBe(request.ErrorCode);
        authoritative.GetDomainEvents().ShouldNotBeEmpty();
        sourceInstance.Status.ShouldBe(InstanceStatus.Busy);
        sourceInstance.GetLoadedIncidents().ShouldBeEmpty();
        await fixture.Repository.Received(1).UpdateAsync(
            authoritative,
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FaultAsync_Stamps_Filtered_Source_Headers_On_SubFaultedEvent()
    {
        var authoritative = CreateBusyInstance();
        authoritative.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = WorkflowType.SubFlow.Code;
        authoritative.ExtraProperties[DomainConsts.MetaDataKeys.Id] = Guid.NewGuid().ToString();
        authoritative.ExtraProperties[DomainConsts.MetaDataKeys.Domain] = "parent-domain";
        authoritative.ExtraProperties[DomainConsts.MetaDataKeys.Flow] = "parent-flow";
        authoritative.ExtraProperties[DomainConsts.MetaDataKeys.Version] = "1.0.0";
        var policy = new CarriedHeaderPolicy(
            Options.Create(new SubFlowCarriedHeadersOptions()), NullLogger<CarriedHeaderPolicy>.Instance);
        var fixture = CreateFixture(authoritative, policy: policy);
        var snapshot = CreateSnapshot(authoritative.Id, headers: new Dictionary<string, string?>
        {
            ["sub"] = "u1", ["Authorization"] = "Bearer x", ["X-Custom"] = "v"
        });

        var result = await fixture.Service.FaultAsync(
            snapshot, new PostCommitFaultRequest("PostCommit:Dependency", "failed", "stack"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var evt = authoritative.GetDomainEvents().Select(e => e.Event).OfType<InstanceSubFaultedEvent>().Single();
        evt.CarriedHeaders.ShouldNotBeNull();
        evt.CarriedHeaders!.OrderBy(k => k.Key).ToDictionary(k => k.Key, k => k.Value)
            .ShouldBe(new Dictionary<string, string?> { ["sub"] = "u1", ["x-custom"] = "v" });
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
        var existingIncidentCount = authoritative.GetLoadedIncidents().Count;
        var fixture = CreateFixture(authoritative);

        var result = await fixture.Service.FaultAsync(
            CreateSnapshot(authoritative.Id),
            new PostCommitFaultRequest("PostCommit:LateFailure", "late failure"),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Status.ShouldBe(expectedStatus);
        authoritative.Status.ShouldBe(expectedStatus);
        authoritative.GetLoadedIncidents().Count.ShouldBe(existingIncidentCount);
        await fixture.Repository.DidNotReceiveWithAnyArgs()
            .UpdateAsync(default!, default, default);
    }

    // vnext#1256: the fault commits in a unit of work no runner relays from, so a sub item's upward
    // InstanceSubFaultedEvent travelled the outbox alone — and never reached a parent in another domain.

    [Fact]
    public async Task FaultAsync_WhenTheInstanceIsASubItem_RelaysItsSubFaultedEvent_AfterTheCommitAndTheUnlock()
    {
        var parentId = Guid.NewGuid();
        var authoritative = CreateBusySubFlowInstance(parentId);
        var calls = new List<string>();
        IReadOnlyList<DomainEventEnvelope> relayed = [];
        var dispatcher = Substitute.For<IPostCommitRelayDispatcher>();
        dispatcher.RelayAsync(Arg.Any<IReadOnlyList<DomainEventEnvelope>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                calls.Add("relay");
                relayed = call.ArgAt<IReadOnlyList<DomainEventEnvelope>>(0);
                return Task.CompletedTask;
            });
        // The save drains the aggregate, as the real sink does: the relay must get the snapshot.
        var fixture = CreateFixture(authoritative, calls, relayDispatcher: dispatcher, clearEventsOnSave: true);

        var result = await fixture.Service.FaultAsync(
            CreateSnapshot(authoritative.Id),
            new PostCommitFaultRequest("PostCommit:Dependency", "child invocation failed"),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        // The parent's settlement must start only once the fault is durable and the lock is free.
        calls.SkipWhile(call => call != "commit").ShouldBe(new[] { "commit", "unlock", "relay" });
        var evt = relayed.Select(e => e.Event).OfType<InstanceSubFaultedEvent>().Single();
        evt.InstanceId.ShouldBe(parentId);
        evt.SubInstanceId.ShouldBe(authoritative.Id);
        evt.Domain.ShouldBe("parent-domain");
    }

    [Fact]
    public async Task FaultAsync_WhenTheLockIsNotAcquired_RelaysNothing()
    {
        var dispatcher = Substitute.For<IPostCommitRelayDispatcher>();
        var fixture = CreateFixture(
            CreateBusySubFlowInstance(Guid.NewGuid()), lockAcquired: false, relayDispatcher: dispatcher);

        var result = await fixture.Service.FaultAsync(
            CreateSnapshot(Guid.NewGuid()),
            new PostCommitFaultRequest("PostCommit:Dependency", "child invocation failed"),
            CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        await dispatcher.DidNotReceiveWithAnyArgs().RelayAsync(default!, default);
    }

    [Fact]
    public async Task FaultAsync_WhenTheInstanceIsAlreadyTerminal_RelaysNothing()
    {
        // No fault was written here, so there is no event of this call's to deliver.
        var authoritative = CreateBusySubFlowInstance(Guid.NewGuid());
        authoritative.Complete(Domain, sync: true);
        authoritative.ClearDomainEvents();
        var dispatcher = Substitute.For<IPostCommitRelayDispatcher>();
        var fixture = CreateFixture(authoritative, relayDispatcher: dispatcher);

        var result = await fixture.Service.FaultAsync(
            CreateSnapshot(authoritative.Id),
            new PostCommitFaultRequest("PostCommit:LateFailure", "late failure"),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await dispatcher.DidNotReceiveWithAnyArgs().RelayAsync(default!, default);
    }

    [Fact]
    public async Task FaultAsync_WhenTheCommitThrows_RelaysNothing()
    {
        var dispatcher = Substitute.For<IPostCommitRelayDispatcher>();
        var authoritative = CreateBusySubFlowInstance(Guid.NewGuid());
        var fixture = CreateFixture(authoritative, relayDispatcher: dispatcher, failCommit: true);

        await Should.ThrowAsync<InvalidOperationException>(() => fixture.Service.FaultAsync(
            CreateSnapshot(authoritative.Id),
            new PostCommitFaultRequest("PostCommit:Dependency", "child invocation failed"),
            CancellationToken.None));

        await dispatcher.DidNotReceiveWithAnyArgs().RelayAsync(default!, default);
    }

    // vnext#1256 — the start compensation steps aside when the child it created already faulted and
    // settled this parent: the parent's error boundary decided where the parent rests.

    [Fact]
    public async Task FaultAsync_WhenTheStartedChildAlreadySettledItsFault_LeavesTheParentWhereItsBoundaryPutIt()
    {
        // The child's settlement closed the correlation with outcome Faulted and routed the parent
        // to an Active recovery state; the reload carries open correlations only, so the closed one
        // is read by id.
        var authoritative = Instance.Create(Guid.NewGuid(), WorkflowKey, WorkflowVersion, "instance-key");
        var correlation = ClosedCorrelation(authoritative.Id, SubFlowType.SubFlow, SubItemTerminalOutcome.Faulted);
        var fixture = CreateFixture(authoritative, correlationRepository: CorrelationsReturning(correlation));

        var result = await FaultForStartedCorrelationAsync(fixture, authoritative, correlation.Id);

        result.IsSuccess.ShouldBeTrue();
        authoritative.Status.ShouldBe(InstanceStatus.Active);
        authoritative.GetLoadedIncidents().ShouldBeEmpty();
        await fixture.Repository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default, default);
    }

    // Every case but "closed by the child's own fault, and settled" faults the parent, as on master
    // (fail-safe).

    [Fact]
    public async Task FaultAsync_WhenTheChildsFaultClosedTheCorrelationButHasNotSettledYet_FaultsTheParent()
    {
        // SubflowFaultService commits the correlation closed + Faulted BEFORE the parent's boundary
        // transition runs; SettledAt is written only after that transition succeeded. Stepping aside
        // in between would leave the parent Busy with no owner if the boundary then fails.
        var authoritative = CreateBusyInstance();
        var correlation = ClosedCorrelation(
            authoritative.Id, SubFlowType.SubFlow, SubItemTerminalOutcome.Faulted, settled: false);
        correlation.SettledAt.ShouldBeNull();
        var fixture = CreateFixture(authoritative, correlationRepository: CorrelationsReturning(correlation));

        var result = await FaultForStartedCorrelationAsync(fixture, authoritative, correlation.Id);

        result.IsSuccess.ShouldBeTrue();
        authoritative.Status.ShouldBe(InstanceStatus.Faulted);
        authoritative.GetLoadedIncidents().Single().ErrorCode.ShouldBe(WorkflowErrorCodes.ResourceLockConflict);
    }

    [Fact]
    public async Task FaultAsync_ReadsTheStartedCorrelationByItsOwnId_WithoutTracking()
    {
        // The stub answers only for the named correlation id: a guard that asked for any other id
        // (the sub instance id, the parent id) would read null and fault the parent.
        var authoritative = Instance.Create(Guid.NewGuid(), WorkflowKey, WorkflowVersion, "instance-key");
        var correlation = ClosedCorrelation(authoritative.Id, SubFlowType.SubFlow, SubItemTerminalOutcome.Faulted);
        var repository = CorrelationsReturning(correlation);
        var fixture = CreateFixture(authoritative, correlationRepository: repository);

        await FaultForStartedCorrelationAsync(fixture, authoritative, correlation.Id);

        await repository.Received(1).FindByIdAsReadOnlyAsync(correlation.Id, Arg.Any<CancellationToken>());
        // The tracked finder would attach the closed row to the tracked parent's open-only list.
        await repository.DidNotReceive().FindAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        authoritative.ChildCorrelations.ShouldBeEmpty();
    }

    [Fact]
    public async Task FaultAsync_WhenTheStartedCorrelationDoesNotExist_FaultsTheParent()
    {
        // SubFlowCorrelationNotFound from the start handler: no child, no open correlation. Skipping
        // here would leave the parent Busy with no owner — the #1256 symptom by another road.
        var authoritative = CreateBusyInstance();
        var fixture = CreateFixture(authoritative, correlationRepository: CorrelationsReturning(null));

        await FaultForStartedCorrelationAsync(fixture, authoritative, Guid.NewGuid());

        authoritative.Status.ShouldBe(InstanceStatus.Faulted);
    }

    [Fact]
    public async Task FaultAsync_WhenTheStartedChildCompleted_FaultsTheParentAsBefore()
    {
        var authoritative = CreateBusyInstance();
        var correlation = ClosedCorrelation(authoritative.Id, SubFlowType.SubFlow, SubItemTerminalOutcome.Completed);
        var fixture = CreateFixture(authoritative, correlationRepository: CorrelationsReturning(correlation));

        await FaultForStartedCorrelationAsync(fixture, authoritative, correlation.Id);

        authoritative.Status.ShouldBe(InstanceStatus.Faulted);
    }

    [Fact]
    public async Task FaultAsync_WhenTheStartedCorrelationIsAFaultedSubProcess_FaultsTheParent()
    {
        // A SubProcess fault only closes its correlation; no boundary of the parent ran.
        var authoritative = CreateBusyInstance();
        var correlation = ClosedCorrelation(authoritative.Id, SubFlowType.SubProcess, SubItemTerminalOutcome.Faulted);
        var fixture = CreateFixture(authoritative, correlationRepository: CorrelationsReturning(correlation));

        await FaultForStartedCorrelationAsync(fixture, authoritative, correlation.Id);

        authoritative.Status.ShouldBe(InstanceStatus.Faulted);
    }

    [Fact]
    public async Task FaultAsync_WhenTheFaultedCorrelationBelongsToAnotherParent_FaultsTheParent()
    {
        var authoritative = CreateBusyInstance();
        var correlation = ClosedCorrelation(Guid.NewGuid(), SubFlowType.SubFlow, SubItemTerminalOutcome.Faulted);
        var fixture = CreateFixture(authoritative, correlationRepository: CorrelationsReturning(correlation));

        await FaultForStartedCorrelationAsync(fixture, authoritative, correlation.Id);

        authoritative.Status.ShouldBe(InstanceStatus.Faulted);
    }

    [Fact]
    public async Task FaultAsync_WithoutACorrelationRepository_NeverStepsAside()
    {
        var authoritative = CreateBusyInstance();
        var fixture = CreateFixture(authoritative);

        await FaultForStartedCorrelationAsync(fixture, authoritative, Guid.NewGuid());

        authoritative.Status.ShouldBe(InstanceStatus.Faulted);
    }

    private static Task<Result<TransitionOutput>> FaultForStartedCorrelationAsync(
        Fixture fixture, Instance authoritative, Guid correlationId) =>
        fixture.Service.FaultAsync(
            CreateSnapshot(authoritative.Id),
            new PostCommitFaultRequest(
                WorkflowErrorCodes.ResourceLockConflict, "child start refused",
                StartedSubflowCorrelationId: correlationId),
            CancellationToken.None);

    /// <summary>
    /// A correlation of <paramref name="parentId"/> closed with <paramref name="outcome"/>, and —
    /// unless <paramref name="settled"/> is false — carrying the marker its settlement writes last.
    /// </summary>
    private static InstanceCorrelation ClosedCorrelation(
        Guid parentId, SubFlowType type, SubItemTerminalOutcome outcome, bool settled = true)
    {
        var owner = Instance.Create(parentId, WorkflowKey, WorkflowVersion, "owner");
        var subInstanceId = Guid.NewGuid();
        owner.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(), parentId, "state", subInstanceId, type.Code, "partner", "child-flow", "1.0.0"));
        var correlation = owner.CompleteCorrelation(subInstanceId, outcome)!;
        if (settled)
            correlation.MarkSettled(DateTime.UtcNow);
        return correlation;
    }

    /// <summary>
    /// A repository whose untracked by-id read answers <paramref name="correlation"/> for THAT
    /// correlation's id and null for every other id (and for every id when it is null).
    /// </summary>
    private static IInstanceCorrelationRepository CorrelationsReturning(InstanceCorrelation? correlation)
    {
        var repository = Substitute.For<IInstanceCorrelationRepository>();
        repository.FindByIdAsReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<InstanceCorrelation?>(null));
        if (correlation is not null)
        {
            repository.FindByIdAsReadOnlyAsync(correlation.Id, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<InstanceCorrelation?>(correlation));
        }

        return repository;
    }

    [Fact]
    public async Task FaultAsync_WhenTheStartedCorrelationIsStillOpen_FaultsTheParentAsBefore()
    {
        // The child never came to exist (or never reported): its correlation is still open, and the
        // parent has a Busy nobody owns — the compensation applies exactly as before.
        var authoritative = CreateBusyInstance();
        var correlationId = Guid.NewGuid();
        authoritative.AddCorrelation(InstanceCorrelation.Create(
            correlationId, authoritative.Id, "state", Guid.NewGuid(), SubFlowType.SubFlow.Code,
            "partner", "child-flow", "1.0.0"));
        var repository = CorrelationsReturning(null);
        var fixture = CreateFixture(authoritative, correlationRepository: repository);

        var result = await fixture.Service.FaultAsync(
            CreateSnapshot(authoritative.Id),
            new PostCommitFaultRequest(
                WorkflowErrorCodes.ResourceLockConflict, "child start refused",
                StartedSubflowCorrelationId: correlationId),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        authoritative.Status.ShouldBe(InstanceStatus.Faulted);
        authoritative.GetLoadedIncidents().Single().ErrorCode.ShouldBe(WorkflowErrorCodes.ResourceLockConflict);
        // The reload already shows the correlation open: the answer is known without a read.
        await repository.DidNotReceiveWithAnyArgs().FindByIdAsReadOnlyAsync(default, default);
    }

    [Fact]
    public async Task FaultAsync_WithoutAStartedCorrelation_FaultsEvenWithNoOpenCorrelation()
    {
        // Every other post-commit fault keeps its behaviour: the guard keys on the named correlation only.
        var authoritative = CreateBusyInstance();
        var fixture = CreateFixture(authoritative);

        await fixture.Service.FaultAsync(
            CreateSnapshot(authoritative.Id),
            new PostCommitFaultRequest("PostCommit:Dependency", "failed"),
            CancellationToken.None);

        authoritative.Status.ShouldBe(InstanceStatus.Faulted);
    }

    private static Instance CreateBusySubFlowInstance(Guid parentId)
    {
        var instance = CreateBusyInstance();
        instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = WorkflowType.SubFlow.Code;
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Id] = parentId.ToString();
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Domain] = "parent-domain";
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Flow] = "parent-flow";
        instance.ExtraProperties[DomainConsts.MetaDataKeys.Version] = "1.0.0";
        return instance;
    }

    private static Fixture CreateFixture(
        Instance authoritative,
        List<string>? calls = null,
        bool lockAcquired = true,
        Definitions.Workflow? workflow = null,
        ICarriedHeaderPolicy? policy = null,
        IPostCommitRelayDispatcher? relayDispatcher = null,
        bool clearEventsOnSave = false,
        bool failCommit = false,
        IInstanceCorrelationRepository? correlationRepository = null)
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
            if (failCommit)
                throw new InvalidOperationException("commit failed");
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
                // The real save drains the aggregate's events into the outbox (SaveChanges sink).
                if (clearEventsOnSave)
                    authoritative.ClearDomainEvents();
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
            NullLogger<PostCommitParentMutationService>.Instance,
            carriedHeaderPolicy: policy,
            relayDispatcher: relayDispatcher,
            correlationRepository: correlationRepository);

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
        ExecMode callerMode = ExecMode.Sync,
        Dictionary<string, string?>? headers = null) => new(
        Domain,
        WorkflowKey,
        WorkflowVersion,
        instanceId,
        "source-transition",
        callerMode,
        "trace-id",
        headers ?? new Dictionary<string, string?> { ["userId"] = "42" },
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
