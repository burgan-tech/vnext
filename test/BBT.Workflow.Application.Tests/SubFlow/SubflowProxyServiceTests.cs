using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Admission;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Shared;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

/// <summary>
/// <see cref="SubflowProxyService"/>: which requests a parent with an active SubFlow proxies, the
/// input the child receives (the parent's mode, no reserve claim, not trusted), and how the child's
/// answer is mapped back for the client.
/// </summary>
public class SubflowProxyServiceTests
{
    private const string Domain = "parent-domain";
    private const string Flow = "parent-flow";
    private const string Version = "1.0.0";
    private const string CurrentState = "in-subflow";

    private readonly IInstanceCommandGateway _gateway = Substitute.For<IInstanceCommandGateway>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IUnitOfWorkManager _uowManager = Substitute.For<IUnitOfWorkManager>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ILogger<SubflowProxyService> _logger = Substitute.For<ILogger<SubflowProxyService>>();
    private readonly SubflowProxyService _service;

    private readonly Guid _parentId = Guid.NewGuid();
    private readonly ActiveSubFlowRef _child = new(Guid.NewGuid(), "child-domain", "child-flow", "2.0.0");

    public SubflowProxyServiceTests()
    {
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(_uow);
        _logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

        // The real classifier: the proxy must agree with admission on what updateData/cancel/exit are.
        var admission = new TransitionAdmissionService(
            Substitute.For<IInstanceStatusLock>(),
            Substitute.For<IInstanceBusyManager>(),
            Substitute.For<ILogger<TransitionAdmissionService>>());

        _service = new SubflowProxyService(
            admission,
            new SubflowForwardingService(_gateway, Substitute.For<ILogger<SubflowForwardingService>>()),
            _instanceRepository,
            _uowManager,
            _logger);
    }

    // 1
    [Fact]
    public async Task NoActiveSubFlow_IsNotProxied()
    {
        var result = await _service.TryProxyAsync(
            Snapshot(activeSubFlow: null), CreateWorkflow(), "approve", Input(sync: false), CancellationToken.None);

        result.ShouldBeNull();
        await _gateway.DidNotReceiveWithAnyArgs().ForwardTransitionAsync(default, default!, default!, default);
    }

    // 2
    [Theory]
    [InlineData(WellKnownTransitionKeys.UpdateData)]
    [InlineData(WellKnownTransitionKeys.Cancel)]
    [InlineData(WellKnownTransitionKeys.Exit)]
    [InlineData(WellKnownTransitionKeys.Timeout)]
    [InlineData("my-update")] // the configured updateData key, not only the alias
    public async Task WellKnownKeys_AreNotProxied(string key)
    {
        var workflow = CreateWorkflow();
        workflow.SetUpdateData(Transition.Create("my-update", null, "$self", TriggerType.Manual, "Patch"));

        var result = await _service.TryProxyAsync(
            Snapshot(_child), workflow, key, Input(sync: false), CancellationToken.None);

        result.ShouldBeNull();
        await _gateway.DidNotReceiveWithAnyArgs().ForwardTransitionAsync(default, default!, default!, default);
    }

    // 3
    [Fact]
    public async Task ParentSharedTransitionAvailableInCurrentState_IsNotProxied()
    {
        var workflow = CreateWorkflow();
        var shared = Transition.Create("escalate", null, "$self", TriggerType.Manual, "Patch");
        shared.AddAvailableIn(CurrentState);
        workflow.AddSharedTransition(shared);

        var result = await _service.TryProxyAsync(
            Snapshot(_child), workflow, "escalate", Input(sync: false), CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ParentSharedTransitionNotAvailableInCurrentState_IsProxied()
    {
        var workflow = CreateWorkflow();
        var shared = Transition.Create("escalate", null, "$self", TriggerType.Manual, "Patch");
        shared.AddAvailableIn("some-other-state");
        workflow.AddSharedTransition(shared);
        GatewayReturns(Result<TransitionOutput>.Ok(new TransitionOutput { Id = _child.InstanceId, Status = InstanceStatus.Busy }));

        var result = await _service.TryProxyAsync(
            Snapshot(_child), workflow, "escalate", Input(sync: false), CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Value.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task RelayClaimingAChainReserve_IsNotProxied()
    {
        // An older-version parent already flipped this chain; only the owner-reentry path carries the
        // claim on to the leaf.
        var input = Input(sync: true);
        input.ChainReserved = true;

        var result = await _service.TryProxyAsync(
            Snapshot(_child), CreateWorkflow(), "approve", input, CancellationToken.None);

        result.ShouldBeNull();
    }

    // 4
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Forwardable_AsyncCaller_ForwardsAsyncToTheChild_AndAnswersWithTheParentId(bool trusted)
    {
        TransitionInput? forwarded = null;
        _gateway
            .ForwardTransitionAsync(_child.InstanceId, "approve", Arg.Do<TransitionInput>(i => forwarded = i), Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput { Id = _child.InstanceId, Status = InstanceStatus.Busy }));

        var input = Input(sync: false);
        input.TrustedPayload = trusted; // server-only flag travels as is (DirectTrigger stays trusted)
        input.Headers["x-custom"] = "v";
        input.Headers[TelemetryConstants.HeaderNames.ParentInstanceId.ToLowerInvariant()] = "stale";
        input.RouteValues["instance"] = _parentId.ToString();
        input.CorrelationId = "corr-1";
        input.Actor = ExecutionActor.System;

        var result = await _service.TryProxyAsync(
            Snapshot(_child), CreateWorkflow(), "approve", input, CancellationToken.None);

        result.ShouldNotBeNull();
        result!.Value.IsSuccess.ShouldBeTrue();
        result.Value.Value!.Id.ShouldBe(_parentId);
        result.Value.Value.Status.ShouldBe(InstanceStatus.Busy);
        result.Value.Value.ExecutedAsync.ShouldBe(true);

        forwarded.ShouldNotBeNull();
        forwarded!.Domain.ShouldBe(_child.Domain);
        forwarded.Workflow.ShouldBe(_child.Flow);
        forwarded.Sync.ShouldBeFalse();
        forwarded.SuppressResponseEnrichment.ShouldBeTrue();
        forwarded.ChainReserved.ShouldBeFalse();
        forwarded.TrustedPayload.ShouldBe(trusted);
        forwarded.Data!.Key.ShouldBe("data-key");
        forwarded.Data.Attributes!.Value.GetProperty("amount").GetInt32().ShouldBe(5);
        forwarded.Headers["x-custom"].ShouldBe("v");
        // One parent-id header, the current parent's, whatever casing an upstream hop used.
        forwarded.Headers[TelemetryConstants.HeaderNames.ParentInstanceId].ShouldBe(_parentId.ToString());
        forwarded.RouteValues["instance"].ShouldBe(_parentId.ToString());
        forwarded.CorrelationId.ShouldBe("corr-1");
        forwarded.Actor.ShouldBe(ExecutionActor.System);
    }

    // 5
    [Fact]
    public async Task ParentTransitionExecutionTypeSync_OverridesAsyncCaller()
    {
        // The parent decides: a parent transition (here a shared one offered elsewhere, so it is
        // forwarded) defined sync wins over the caller's sync=false.
        var workflow = CreateWorkflow();
        var shared = System.Text.Json.JsonSerializer.Deserialize<Transition>("""
            {
                "key": "approve", "from": null, "target": "$self", "triggerType": "manual",
                "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [],
                "executionType": "S", "availableIn": ["some-other-state"]
            }
            """, BBT.Workflow.JsonSerializerConstants.JsonOptions)!;
        workflow.AddSharedTransition(shared);

        TransitionInput? forwarded = null;
        _gateway
            .ForwardTransitionAsync(_child.InstanceId, "approve", Arg.Do<TransitionInput>(i => forwarded = i), Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput { Id = _child.InstanceId, Status = InstanceStatus.Active }));

        var result = await _service.TryProxyAsync(
            Snapshot(_child), workflow, "approve", Input(sync: false), CancellationToken.None);

        forwarded!.Sync.ShouldBeTrue();
        result!.Value.Value!.ExecutedAsync.ShouldBe(false);
    }

    [Fact]
    public async Task RuntimeInternalRelay_KeepsTheModeDecidedAbove()
    {
        // An intermediate level reached by a proxy: SuppressResponseEnrichment carries the mode the
        // level above resolved, and this level's own executionType must not change it.
        var workflow = CreateWorkflow();
        var shared = System.Text.Json.JsonSerializer.Deserialize<Transition>("""
            {
                "key": "approve", "from": null, "target": "$self", "triggerType": "manual",
                "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [],
                "executionType": "S", "availableIn": ["some-other-state"]
            }
            """, BBT.Workflow.JsonSerializerConstants.JsonOptions)!;
        workflow.AddSharedTransition(shared);
        TransitionInput? forwarded = null;
        _gateway
            .ForwardTransitionAsync(_child.InstanceId, "approve", Arg.Do<TransitionInput>(i => forwarded = i), Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput { Id = _child.InstanceId, Status = InstanceStatus.Busy }));

        var input = Input(sync: false);
        input.SuppressResponseEnrichment = true;

        await _service.TryProxyAsync(Snapshot(_child), workflow, "approve", input, CancellationToken.None);

        forwarded!.Sync.ShouldBeFalse();
    }

    // 6
    [Theory]
    [InlineData(WorkflowErrorCodes.InstanceCompleted)]
    [InlineData(WorkflowErrorCodes.InstanceNotFound)]
    public async Task ChildCompletedOrNotFound_MapsToBusy409NamingTheParent(string childCode)
    {
        // The completion window (child finished) or a child row not created yet behind an open
        // correlation: the 409 names the instance the client called, at every level.
        GatewayReturns(Result<TransitionOutput>.Fail(childCode == WorkflowErrorCodes.InstanceNotFound
            ? Error.NotFound(childCode, "no child row", _child.InstanceId.ToString())
            : Error.Validation(childCode, "already completed", _child.InstanceId.ToString())));

        var result = await _service.TryProxyAsync(
            Snapshot(_child), CreateWorkflow(), "approve", Input(sync: false), CancellationToken.None);

        result!.Value.IsSuccess.ShouldBeFalse();
        result.Value.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceBusy);
        result.Value.Error.Prefix.ShouldBe(ErrorCodes.Prefixes.Conflict);
        result.Value.Error.Target.ShouldBe(_parentId.ToString());
    }

    // 7
    [Theory]
    [InlineData("validation")]
    [InlineData("conflict")]
    [InlineData("transient")]
    public async Task OtherChildErrors_AreReturnedAsIs(string kind)
    {
        var error = kind switch
        {
            "validation" => Error.Validation("Transition:100020", "not available"),
            "conflict" => Error.Conflict(WorkflowErrorCodes.InstanceBusy, "busy", _child.InstanceId.ToString()),
            _ => Error.Transient("remote_network_error", "down")
        };
        GatewayReturns(Result<TransitionOutput>.Fail(error));

        var result = await _service.TryProxyAsync(
            Snapshot(_child), CreateWorkflow(), "approve", Input(sync: false), CancellationToken.None);

        result!.Value.IsSuccess.ShouldBeFalse();
        result.Value.Error.ShouldBe(error);
    }

    // 8 — async: pre-stamp BEFORE the forward, committed in its own RequiresNew UoW
    [Fact]
    public async Task Async_StampsParentEffectiveStatusBusy_BeforeForwarding_AndCommits()
    {
        GatewayReturns(Result<TransitionOutput>.Ok(new TransitionOutput { Id = _child.InstanceId, Status = InstanceStatus.Busy }));

        var result = await _service.TryProxyAsync(
            Snapshot(_child), CreateWorkflow(), "approve", Input(sync: false), CancellationToken.None);

        result!.Value.IsSuccess.ShouldBeTrue();
        Received.InOrder(() =>
        {
            _uowManager.Begin(Arg.Is<UnitOfWorkOptions>(o => o.Scope == UnitOfWorkScopeOption.RequiresNew));
            _instanceRepository.SetEffectiveStatusAsync(_parentId, InstanceStatus.Busy, Arg.Any<CancellationToken>());
            _uow.CommitAsync(Arg.Any<CancellationToken>());
            _gateway.ForwardTransitionAsync(_child.InstanceId, "approve", Arg.Any<TransitionInput>(), Arg.Any<CancellationToken>());
        });
        // Success: nothing to revert.
        await _instanceRepository.DidNotReceiveWithAnyArgs()
            .TryCompareAndSetEffectiveStatusAsync(default, default!, default!, default);
    }

    [Theory]
    [InlineData("validation")]
    [InlineData("transient")]
    [InlineData("completed")]
    public async Task Async_ForwardFailure_RevertsThePreStampWithACompareAndSet(string kind)
    {
        GatewayReturns(Result<TransitionOutput>.Fail(kind switch
        {
            "validation" => Error.Validation("Transition:100020", "not available"),
            "completed" => Error.Validation(WorkflowErrorCodes.InstanceCompleted, "done"),
            _ => Error.Transient("remote_network_error", "down")
        }));
        _instanceRepository.TryCompareAndSetEffectiveStatusAsync(
                _parentId, InstanceStatus.Busy, InstanceStatus.Active, Arg.Any<CancellationToken>())
            .Returns(true);

        await _service.TryProxyAsync(
            Snapshot(_child, effectiveStatus: InstanceStatus.Active), CreateWorkflow(), "approve",
            Input(sync: false), CancellationToken.None);

        Received.InOrder(() =>
        {
            _instanceRepository.SetEffectiveStatusAsync(_parentId, InstanceStatus.Busy, Arg.Any<CancellationToken>());
            _uow.CommitAsync(Arg.Any<CancellationToken>());
            _gateway.ForwardTransitionAsync(_child.InstanceId, "approve", Arg.Any<TransitionInput>(), Arg.Any<CancellationToken>());
            // Back to the projection the snapshot read, only while the column still holds our Busy.
            _instanceRepository.TryCompareAndSetEffectiveStatusAsync(
                _parentId, InstanceStatus.Busy, InstanceStatus.Active, Arg.Any<CancellationToken>());
            _uow.CommitAsync(Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Async_ForwardThrows_RevertsAndRethrows()
    {
        _gateway
            .ForwardTransitionAsync(_child.InstanceId, Arg.Any<string>(), Arg.Any<TransitionInput>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<TransitionOutput>>>(_ => throw new InvalidOperationException("boom"));

        await Should.ThrowAsync<InvalidOperationException>(() => _service.TryProxyAsync(
            Snapshot(_child, effectiveStatus: InstanceStatus.Active), CreateWorkflow(), "approve",
            Input(sync: false), CancellationToken.None));

        await _instanceRepository.Received(1).TryCompareAndSetEffectiveStatusAsync(
            _parentId, InstanceStatus.Busy, InstanceStatus.Active, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Async_ChildOwnInstanceBusy_KeepsTheStamp()
    {
        // The child really is busy: the Busy projection is true, so nothing is reverted.
        GatewayReturns(Result<TransitionOutput>.Fail(Error.Conflict(
            WorkflowErrorCodes.InstanceBusy, "busy", _child.InstanceId.ToString())));

        var result = await _service.TryProxyAsync(
            Snapshot(_child, effectiveStatus: InstanceStatus.Active), CreateWorkflow(), "approve",
            Input(sync: false), CancellationToken.None);

        result!.Value.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceBusy);
        await _instanceRepository.Received(1)
            .SetEffectiveStatusAsync(_parentId, InstanceStatus.Busy, Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceiveWithAnyArgs()
            .TryCompareAndSetEffectiveStatusAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task Async_RevertCasNoOp_WhenTheColumnAlreadyMoved_IsHarmless()
    {
        // The child's relay wrote a newer value; the CAS matches nothing and the error still returns.
        GatewayReturns(Result<TransitionOutput>.Fail(Error.Validation("Transition:100020", "not available")));
        _instanceRepository.TryCompareAndSetEffectiveStatusAsync(
                Arg.Any<Guid>(), Arg.Any<InstanceStatus>(), Arg.Any<InstanceStatus>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _service.TryProxyAsync(
            Snapshot(_child, effectiveStatus: InstanceStatus.Active), CreateWorkflow(), "approve",
            Input(sync: false), CancellationToken.None);

        result!.Value.Error.Code.ShouldBe("Transition:100020");
        await _instanceRepository.Received(1).TryCompareAndSetEffectiveStatusAsync(
            _parentId, InstanceStatus.Busy, InstanceStatus.Active, Arg.Any<CancellationToken>());
        await _instanceRepository.Received(1)
            .SetEffectiveStatusAsync(_parentId, InstanceStatus.Busy, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Async_PriorProjectionAlreadyBusy_NoRevert()
    {
        GatewayReturns(Result<TransitionOutput>.Fail(Error.Validation("Transition:100020", "not available")));

        await _service.TryProxyAsync(
            Snapshot(_child, effectiveStatus: InstanceStatus.Busy), CreateWorkflow(), "approve",
            Input(sync: false), CancellationToken.None);

        await _instanceRepository.DidNotReceiveWithAnyArgs()
            .TryCompareAndSetEffectiveStatusAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task Async_PreStampFailure_IsSwallowedAndLogged_ForwardStillRuns_NoRevert()
    {
        _instanceRepository.SetEffectiveStatusAsync(_parentId, InstanceStatus.Busy, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("db down"));
        GatewayReturns(Result<TransitionOutput>.Fail(Error.Validation("Transition:100020", "not available")));

        var result = await _service.TryProxyAsync(
            Snapshot(_child, effectiveStatus: InstanceStatus.Active), CreateWorkflow(), "approve",
            Input(sync: false), CancellationToken.None);

        result!.Value.Error.Code.ShouldBe("Transition:100020");
        await _gateway.Received(1).ForwardTransitionAsync(
            _child.InstanceId, "approve", Arg.Any<TransitionInput>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceiveWithAnyArgs().CommitAsync(default);
        // Nothing was stamped, so there is nothing of ours to revert.
        await _instanceRepository.DidNotReceiveWithAnyArgs()
            .TryCompareAndSetEffectiveStatusAsync(default, default!, default!, default);
        System.Linq.Enumerable.Count(_logger.ReceivedCalls(), c =>
                c.GetMethodInfo().Name == nameof(ILogger.Log)
                && (LogLevel)c.GetArguments()[0]! == LogLevel.Warning
                && ((EventId)c.GetArguments()[1]!).Id == 40109
                && c.GetArguments()[3] is InvalidOperationException)
            .ShouldBe(1);
    }

    [Fact]
    public async Task Sync_NeverStamps_TheChildRelayOwnsTheProjection()
    {
        GatewayReturns(Result<TransitionOutput>.Ok(new TransitionOutput { Id = _child.InstanceId, Status = InstanceStatus.Busy }));

        await _service.TryProxyAsync(
            Snapshot(_child), CreateWorkflow(), "approve", Input(sync: true), CancellationToken.None);

        await _instanceRepository.DidNotReceiveWithAnyArgs().SetEffectiveStatusAsync(default, default!, default);
        _uowManager.DidNotReceiveWithAnyArgs().Begin(default!);
    }

    [Fact]
    public async Task ChildCompletedInSyncForward_AnswersWithTheParentsFreshStatus()
    {
        // The child's completion resumed the parent in another scope; only a fresh read says where.
        GatewayReturns(Result<TransitionOutput>.Ok(new TransitionOutput { Id = _child.InstanceId, Status = InstanceStatus.Completed }));
        var parent = Instance.Create(_parentId, Flow, Version, "pk");
        _instanceRepository.FindByIdentifierSlimAsync(_parentId.ToString(), Arg.Any<CancellationToken>())
            .Returns(parent);

        var result = await _service.TryProxyAsync(
            Snapshot(_child), CreateWorkflow(), "approve", Input(sync: true), CancellationToken.None);

        result!.Value.Value!.Status.ShouldBe(parent.Status);
    }

    private void GatewayReturns(Result<TransitionOutput> result)
        => _gateway
            .ForwardTransitionAsync(_child.InstanceId, Arg.Any<string>(), Arg.Any<TransitionInput>(), Arg.Any<CancellationToken>())
            .Returns(result);

    private InstanceExecutionSnapshot Snapshot(ActiveSubFlowRef? activeSubFlow, InstanceStatus? effectiveStatus = null)
        => new(_parentId, "pk", InstanceStatus.Busy, CurrentState, Flow, Version,
            HasActiveSubFlow: activeSubFlow is not null, ActiveSubFlow: activeSubFlow,
            EffectiveStatus: effectiveStatus ?? InstanceStatus.Active);

    private static TransitionInput Input(bool sync)
        => new(Domain, Flow, new TransitionDataInput(
            System.Text.Json.JsonDocument.Parse("""{"amount":5}""").RootElement.Clone()) { Key = "data-key" }, sync)
        {
            Headers = new Dictionary<string, string?>(),
            RouteValues = new Dictionary<string, string?>()
        };

    private static Definitions.Workflow CreateWorkflow()
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(Flow, Domain, "sys-flows", Version));
        workflow.SetStartTransition(Transition.Create("start", null, "s1", TriggerType.Manual, "Patch"));
        return workflow;
    }
}
