using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Guids;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// Unit tests for <see cref="HandleLongPollTerminationStep"/> — the declarative long-poll
/// termination pause point.
/// </summary>
public class HandleLongPollTerminationStepTests
{
    private const string Domain = "test-domain";
    private const string WorkflowKey = "test-workflow";

    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IInstanceJobRepository _jobRepository = Substitute.For<IInstanceJobRepository>();
    private readonly IBackgroundJobService _jobService = Substitute.For<IBackgroundJobService>();
    private readonly IGuidGenerator _guidGenerator = Substitute.For<IGuidGenerator>();
    private readonly ITransitionAuthorizationManager _authManager = Substitute.For<ITransitionAuthorizationManager>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly HandleLongPollTerminationStep _step;

    public HandleLongPollTerminationStepTests()
    {
        _jobService.EnqueueAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<LongPollAckTimeoutPayload>(),
                Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(),
                Arg.Any<JobScheduleFailurePolicy?>(), Arg.Any<bool>(),
                Arg.Any<Guid?>(), Arg.Any<JobKind?>(),
                Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
        _guidGenerator.Create().Returns(Guid.NewGuid());
        _step = new HandleLongPollTerminationStep(
            _instanceRepository, _jobRepository, _jobService, _guidGenerator,
            _authManager, new DefaultCallerRoleResolver(_currentUser),
            Substitute.For<ILogger<HandleLongPollTerminationStep>>());
    }

    [Fact]
    public void Order_ShouldBeLongPollTermination()
    {
        _step.Order.ShouldBe(LifecycleOrder.LongPollTermination);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStateDoesNotTerminateLongPoll_ShouldContinue()
    {
        var workflow = CreateWorkflow(terminate: false);
        var context = CreateContext(workflow, workflow.GetState("review").Value!);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.SkipToOrder.ShouldBeNull();
        context.Instance.IsAwaitingLongPollAck.ShouldBeFalse();
        await _jobService.DidNotReceiveWithAnyArgs().EnqueueAsync<LongPollAckTimeoutPayload>(
            default!, default!, default!, default!, default, default, default, default, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStateTerminatesLongPoll_ShouldArmTokenScheduleFallbackAndPause()
    {
        var workflow = CreateWorkflow(terminate: true);
        var context = CreateContext(workflow, workflow.GetState("review").Value!);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Pause: skip the epilogue to Finalize.
        result.IsSuccess.ShouldBeTrue();
        result.Value!.SkipToOrder.ShouldBe(LifecycleOrder.Finalize);

        // Token armed and persisted; fallback job scheduled and tracked.
        context.Instance.IsAwaitingLongPollAck.ShouldBeTrue();
        await _instanceRepository.Received(1).UpdateAsync(context.Instance, true, Arg.Any<CancellationToken>());
        await _jobService.Received(1).EnqueueAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<LongPollAckTimeoutPayload>(),
            Arg.Any<string>(), Arg.Any<Dictionary<string, object>>(),
            Arg.Any<JobScheduleFailurePolicy?>(), directly: true,
            Arg.Any<Guid?>(), Arg.Any<JobKind?>(),
            cancellationToken: Arg.Any<CancellationToken>());
        await _jobRepository.Received(1).InsertAsync(Arg.Any<InstanceJob>(), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenAlreadyAwaitingAck_ShouldNotReArm()
    {
        var workflow = CreateWorkflow(terminate: true);
        var context = CreateContext(workflow, workflow.GetState("review").Value!);
        context.Instance.ArmLongPollAck(Guid.NewGuid());

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.SkipToOrder.ShouldBeNull(); // not applicable → Continue
        await _jobService.DidNotReceiveWithAnyArgs().EnqueueAsync<LongPollAckTimeoutPayload>(
            default!, default!, default!, default!, default, default, default, default, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRolesConfiguredAndCallerOwns_ShouldArmAndPause()
    {
        var workflow = CreateWorkflow(terminate: true, withRoles: true);
        var context = CreateContext(workflow, workflow.GetState("review").Value!);
        _authManager.IsAnyRoleAllowedForGrantsAsync(
                Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<IReadOnlyCollection<RoleGrant>>(),
                Arg.Any<Instance?>(), Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.SkipToOrder.ShouldBe(LifecycleOrder.Finalize);
        context.Instance.IsAwaitingLongPollAck.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_WhenRolesConfiguredAndCallerDoesNotOwn_ShouldNotArm()
    {
        var workflow = CreateWorkflow(terminate: true, withRoles: true);
        var context = CreateContext(workflow, workflow.GetState("review").Value!);
        _authManager.IsAnyRoleAllowedForGrantsAsync(
                Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<IReadOnlyCollection<RoleGrant>>(),
                Arg.Any<Instance?>(), Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Not an owning role → no pause, no arm, pipeline continues.
        result.IsSuccess.ShouldBeTrue();
        result.Value!.SkipToOrder.ShouldBeNull();
        context.Instance.IsAwaitingLongPollAck.ShouldBeFalse();
        await _jobService.DidNotReceiveWithAnyArgs().EnqueueAsync<LongPollAckTimeoutPayload>(
            default!, default!, default!, default!, default, default, default, default, default, default);
    }

    private TransitionExecutionContext CreateContext(Definitions.Workflow workflow, State target)
    {
        var instance = Instance.Create(Guid.NewGuid(), WorkflowKey, "1.0.0");
        instance.ChangeState(target);
        return new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = Domain,
            WorkflowKey = WorkflowKey,
            TransitionKey = "go",
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.System,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = target,
            Target = target,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static Definitions.Workflow CreateWorkflow(bool terminate, bool withRoles = false)
    {
        var roles = withRoles
            ? """, "roles": [ { "role": "morph-idm.core", "grant": "allow" } ]"""
            : "";
        var interaction = terminate
            ? $$""", "interaction": { "longPoll": { "terminate": true, "fallbackTimeoutSeconds": 30{{roles}} } }"""
            : "";
        var json = $$"""
                   {
                       "type": "F",
                       "timeout": null,
                       "labels": [],
                       "functions": [],
                       "features": [],
                       "states": [
                           { "key": "review", "stateType": "Intermediate", "transitions": []{{interaction}} }
                       ],
                       "sharedTransitions": [],
                       "extensions": [],
                       "startTransition": {"key": "start", "from": null, "target": "review", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
                   }
                   """;
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference(WorkflowKey, Domain, "sys-flows", "1.0.0"));
        return workflow;
    }
}
