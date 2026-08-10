using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.DefinitionContext;
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
            null);
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
            CreateContinuations(resolvedStatus: InstanceStatus.Active, endChainRequested: true),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.PipelineInstance.ShouldBeSameAs(authoritative);
        result.Value.Status.ShouldBe(InstanceStatus.Active);
        authoritative.Status.ShouldBe(InstanceStatus.Active);
        authoritative.ChainToken.ShouldBeNull();
        sourceInstance.Status.ShouldBe(InstanceStatus.Busy);
        sourceInstance.ChainToken.ShouldNotBeNull();
        calls.ShouldBe(["lock", "uow", "reload", "update", "commit", "unlock"]);
        await fixture.LockScopeFactory.Received(1).AcquireAsync(
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
            CreateContinuations(resolvedStatus: InstanceStatus.Active, endChainRequested: true),
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
        var fixture = CreateFixture(
            authoritative,
            workflow: CreateWorkflow(CreateNotifyingState("callback-settled", StateSubType.None)));

        var result = await fixture.Service.SettleAsync(
            CreateSnapshot(authoritative.Id),
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
    public async Task SettleAsync_ReleasesFreshChainOwnershipWithoutDuplicatingStateNotification()
    {
        var authoritative = CreateBusyInstance();
        authoritative.ChangeState(CreateNotifyingState("fresh-resting-state"));
        var fixture = CreateFixture(authoritative, workflow: CreateWorkflow(CreateNotifyingState("fresh-resting-state")));

        var result = await fixture.Service.SettleAsync(
            CreateSnapshot(authoritative.Id),
            CreateContinuations(endChainRequested: true),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        authoritative.Status.ShouldBe(InstanceStatus.Busy);
        authoritative.ChainToken.ShouldBeNull();
        await fixture.Repository.Received(1).UpdateAsync(
            authoritative,
            true,
            Arg.Any<CancellationToken>());
        await fixture.NotificationScheduler.DidNotReceiveWithAnyArgs()
            .ScheduleAsync(default!, default);
    }

    [Fact]
    public async Task SettleAsync_WhenFreshBusyParentResolvesToActive_SchedulesNotification()
    {
        var authoritative = CreateBusyInstance();
        authoritative.ChangeState(CreateNotifyingState("freshly-resolved", StateSubType.None));
        var fixture = CreateFixture(
            authoritative,
            workflow: CreateWorkflow(CreateNotifyingState("freshly-resolved", StateSubType.None)));

        var result = await fixture.Service.SettleAsync(
            CreateSnapshot(authoritative.Id),
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

        var lockFactory = Substitute.For<ITransitionLockScopeFactory>();
        lockFactory.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
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
        repository.FindWithAllCorrelationsAndDataAsync(authoritative.Id, Arg.Any<CancellationToken>())
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

        var workflowContext = Substitute.For<IWorkflowContext>();
        workflowContext.Workflow.Returns(workflow);
        workflowContext.HasWorkflow.Returns(true);
        var notificationScheduler = Substitute.For<IStateNotificationScheduler>();

        var service = new PostCommitParentMutationService(
            uowManager,
            repository,
            lockFactory,
            Substitute.For<BBT.Workflow.Execution.Pipeline.IInstanceStatusLock>(),
            Microsoft.Extensions.Options.Options.Create(
                new BBT.Workflow.BackgroundJobs.Options.WorkflowExecutionOptions()),
            workflowContext,
            notificationScheduler,
            NullLogger<PostCommitParentMutationService>.Instance);

        return new Fixture(service, repository, uowManager, lockFactory, notificationScheduler);
    }

    private static Instance CreateBusyInstance(Guid? id = null)
    {
        var instance = Instance.Create(id ?? Guid.NewGuid(), WorkflowKey, WorkflowVersion, "instance-key");
        instance.BeginChain(Guid.NewGuid());
        return instance;
    }

    private static PostCommitParentSnapshot CreateSnapshot(Guid instanceId) => new(
        Domain,
        WorkflowKey,
        WorkflowVersion,
        instanceId,
        "source-transition",
        ExecMode.Sync,
        "trace-id",
        new Dictionary<string, string?> { ["userId"] = "42" },
        new Dictionary<string, string?> { ["route"] = "value" },
        null);

    private static ContinuationSet CreateContinuations(
        InstanceStatus? resolvedStatus = null,
        bool endChainRequested = false) => new(
        null,
        Array.Empty<IPostCommitJob>(),
        resolvedStatus,
        null,
        false,
        EpilogueMode.Run,
        endChainRequested);

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
        ITransitionLockScopeFactory LockScopeFactory,
        IStateNotificationScheduler NotificationScheduler);
}
