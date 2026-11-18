using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Results;
using BBT.Workflow.Application.Tests.Execution.Cancellation;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.Definitions;
using BBT.Workflow.Events;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Instances.Related;
using BBT.Workflow.Logging;
using BBT.Workflow.Orchestration.Controllers.Instances;
using BBT.Workflow.Schedule;
using BBT.Workflow.SubFlow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HttpApi;

/// <summary>
/// vnext#1258: the HTTP entry actions detach the run from the request token. A client that disconnects
/// cancels the request token; the app service must still receive a live run token, the span is tagged
/// <c>vnext.client.aborted</c>, and the tag is written even when the run throws.
/// </summary>
public sealed class InstanceControllerRunTokenTests
{
    private const string Domain = "test-domain";
    private const string Flow = "test-flow";

    private readonly IInstanceCommandAppService _commandAppService = Substitute.For<IInstanceCommandAppService>();
    private readonly IInstanceScheduleAppService _scheduleAppService = Substitute.For<IInstanceScheduleAppService>();
    private readonly IInstanceRetryAppService _retryAppService = Substitute.For<IInstanceRetryAppService>();
    private readonly IEventAppService _eventAppService = Substitute.For<IEventAppService>();
    private readonly ISubflowCompletionService _subflowCompletionService = Substitute.For<ISubflowCompletionService>();
    private readonly ISubflowStateService _subflowStateService = Substitute.For<ISubflowStateService>();
    private readonly ISubflowFaultService _subflowFaultService = Substitute.For<ISubflowFaultService>();
    private readonly ISubflowCancellationService _subflowCancellationService = Substitute.For<ISubflowCancellationService>();
    private readonly IChildSubflowCancellationService _childSubflowCancellationService = Substitute.For<IChildSubflowCancellationService>();
    private readonly InstanceController _controller;

    public InstanceControllerRunTokenTests()
    {
        _commandAppService
            .StartAsync(Arg.Any<StartInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<StartInstanceOutput>.Ok(new StartInstanceOutput { Id = Guid.NewGuid(), Status = InstanceStatus.Active }));
        _commandAppService
            .TransitionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TransitionInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput { Id = Guid.NewGuid(), Status = InstanceStatus.Active }));
        _scheduleAppService
            .StartAsync(Arg.Any<ScheduledStartInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<object?>.Ok(null));
        _retryAppService
            .RetryAsync(Arg.Any<RetryInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<RetryInstanceOutput>.Ok(new RetryInstanceOutput()));
        _eventAppService
            .HandleAsync(Arg.Any<EventInput>(), Arg.Any<CancellationToken>())
            .Returns(Result<object?>.Ok(null));
        _commandAppService
            .AcknowledgeLongPollAsync(Arg.Any<AcknowledgeLongPollInput>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());
        _childSubflowCancellationService
            .CancelChildSubflowAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<TerminationContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());

        var services = new ServiceCollection();
        services.AddSingleton<ILazyServiceProvider, LazyServiceProvider>();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);

        _controller = new InstanceController(
            commandAppService: _commandAppService,
            queryAppService: Substitute.For<IInstanceQueryAppService>(),
            retryAppService: _retryAppService,
            httpContextAccessor: accessor,
            subflowCompletionService: _subflowCompletionService,
            subflowStateService: _subflowStateService,
            subflowFaultService: _subflowFaultService,
            subflowCancellationService: _subflowCancellationService,
            cancellationService: Substitute.For<IInstanceCancellationService>(),
            childSubflowCancellationService: _childSubflowCancellationService,
            childSubflowFaultService: Substitute.For<IChildSubflowFaultService>(),
            transitionJobEnqueuer: Substitute.For<ITransitionJobEnqueuer>(),
            instanceCommandGateway: Substitute.For<IInstanceCommandGateway>(),
            eventAppService: _eventAppService,
            scheduleAppService: _scheduleAppService,
            relatedInstanceQueryAppService: Substitute.For<IRelatedInstanceQueryAppService>(),
            humanTaskLeafResolver: Substitute.For<BBT.Workflow.Instances.HumanTask.IHumanTaskLeafResolver>(),
            instanceCorrelationResolver: Substitute.For<BBT.Workflow.Instances.Correlation.IInstanceCorrelationResolver>(),
            callerRoleResolver: new BBT.Workflow.Authorization.DefaultCallerRoleResolver(
                Substitute.For<BBT.Aether.Users.ICurrentUser>()),
            runCancellation: RunCancellationTestDoubles.Live())
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private static CancellationToken CancelledRequestToken()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        return cts.Token;
    }

    [Fact]
    public async Task Start_WhenRequestTokenCancelled_RunsWithLiveToken()
    {
        await _controller.StartAsync(Domain, Flow, body: null, sync: true, cancellationToken: CancelledRequestToken());

        await _commandAppService.Received(1).StartAsync(
            Arg.Any<StartInstanceInput>(), Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    [Fact]
    public async Task StartSub_WhenRequestTokenCancelled_RunsWithLiveToken()
    {
        await _controller.StartSubAsync(Domain, Flow, new CreateSubInstanceDto { Id = Guid.NewGuid(), ExtraProperties = new() },
            sync: true, cancellationToken: CancelledRequestToken());

        await _commandAppService.Received(1).StartAsync(
            Arg.Any<StartInstanceInput>(), Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    [Fact]
    public async Task Transition_WhenRequestTokenCancelled_RunsWithLiveToken()
    {
        var instance = Guid.NewGuid().ToString();

        await _controller.TransitionAsync(Domain, Flow, instance, "approve", sync: true,
            cancellationToken: CancelledRequestToken());

        await _commandAppService.Received(1).TransitionAsync(
            instance, "approve", Arg.Any<TransitionInput>(),
            Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    [Fact]
    public async Task SubflowForward_WhenRequestTokenCancelled_RunsWithLiveToken()
    {
        var instance = Guid.NewGuid();

        await _controller.SubflowForwardAsync(Domain, Flow, instance, "approve",
            new SubflowForwardInput { Sync = true }, CancelledRequestToken());

        await _commandAppService.Received(1).TransitionAsync(
            instance.ToString(), "approve", Arg.Any<TransitionInput>(),
            Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    [Fact]
    public async Task ScheduleStart_WhenRequestTokenCancelled_RunsWithLiveToken()
    {
        await _controller.ScheduleStartAsync(Domain, Flow, cancellationToken: CancelledRequestToken());

        await _scheduleAppService.Received(1).StartAsync(
            Arg.Any<ScheduledStartInput>(), Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    [Fact]
    public async Task Retry_WhenRequestTokenCancelled_RunsWithLiveToken()
    {
        await _controller.RetryAsync(Domain, Flow, Guid.NewGuid().ToString(), sync: true,
            cancellationToken: CancelledRequestToken());

        await _retryAppService.Received(1).RetryAsync(
            Arg.Any<RetryInstanceInput>(), Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    [Theory]
    [InlineData("start", null)]
    [InlineData("transition", "approve")]
    public async Task HandleEvent_WhenClientDisconnectsDuringRun_RunTokenStaysLive(string action, string? transitionKey)
    {
        // The body is read on the request token on purpose (nothing is committed yet), so the client
        // disconnects mid-run here: once the run has started, the token it holds must not follow it.
        using var request = new CancellationTokenSource();
        bool? runTokenCancelled = null;
        _eventAppService
            .HandleAsync(Arg.Any<EventInput>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                request.Cancel();
                runTokenCancelled = call.Arg<CancellationToken>().IsCancellationRequested;
                return Result<object?>.Ok(null);
            });

        await _controller.HandleEventAsync(Domain, Flow, action, transitionKey, sync: true,
            cancellationToken: request.Token);

        runTokenCancelled.ShouldBe(false);
    }

    [Fact]
    public async Task AcknowledgeLongPoll_WhenRequestTokenCancelled_RunsWithLiveToken()
    {
        await _controller.AcknowledgeLongPollAsync(Domain, Flow, Guid.NewGuid().ToString(),
            cancellationToken: CancelledRequestToken());

        await _commandAppService.Received(1).AcknowledgeLongPollAsync(
            Arg.Any<AcknowledgeLongPollInput>(), Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    [Fact]
    public async Task ChildCancel_WhenRequestTokenCancelled_RunsWithLiveToken()
    {
        var instance = Guid.NewGuid();

        await _controller.ChildCancelAsync(Domain, Flow, instance,
            new ChildSubflowCancelInput(null, TerminationContext.Direct(instance)), CancelledRequestToken());

        await _childSubflowCancellationService.Received(1).CancelChildSubflowAsync(
            instance, Domain, Flow, Arg.Any<string?>(), Arg.Any<TerminationContext>(),
            Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    [Fact]
    public async Task CompleteSub_WhenRequestTokenCancelled_RunsWithLiveToken()
    {
        var input = new FlowCompletedInput
        {
            InstanceId = Guid.NewGuid(), Domain = Domain, Flow = Flow, Version = null,
            SubInstanceId = Guid.NewGuid(), CompletedState = "done", CompletedAt = DateTime.UtcNow
        };

        await _controller.CompleteSubAsync(Domain, Flow, input.InstanceId.ToString(), input, CancelledRequestToken());

        await _subflowCompletionService.Received(1).CompletionAsync(
            input, Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    [Fact]
    public async Task UpdateSubFlowState_WhenRequestTokenCancelled_RunsWithLiveToken()
    {
        var input = new SubFlowStateChangedInput
        {
            ParentInstanceId = Guid.NewGuid(), SubInstanceId = Guid.NewGuid(), Domain = Domain, Flow = Flow,
            Version = null, NewState = "next", PreviousState = "first", NewStateType = StateType.Intermediate,
            NewStateSubType = StateSubType.None, ChangedAt = DateTime.UtcNow
        };

        await _controller.UpdateSubFlowStateAsync(Domain, Flow, input.ParentInstanceId.ToString(), input,
            CancelledRequestToken());

        await _subflowStateService.Received(1).UpdateParentStateAsync(
            input, Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    [Fact]
    public async Task FaultSub_WhenRequestTokenCancelled_RunsWithLiveToken()
    {
        var input = new SubFlowFaultedInput
        {
            InstanceId = Guid.NewGuid(), Domain = Domain, Flow = Flow, Version = null,
            SubInstanceId = Guid.NewGuid(), FaultedState = "failed", FaultedAt = DateTime.UtcNow
        };

        await _controller.FaultSubAsync(Domain, Flow, input.InstanceId.ToString(), input, CancelledRequestToken());

        await _subflowFaultService.Received(1).FaultAsync(
            input, Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    [Fact]
    public async Task CancelSub_WhenRequestTokenCancelled_RunsWithLiveToken()
    {
        var input = new SubItemCanceledInput
        {
            InstanceId = Guid.NewGuid(), SubInstanceId = Guid.NewGuid(), Domain = Domain, Flow = Flow,
            CanceledState = "cancelled", CanceledAt = DateTime.UtcNow
        };

        await _controller.CancelSubAsync(Domain, Flow, input.InstanceId.ToString(), input, CancelledRequestToken());

        await _subflowCancellationService.Received(1).CancellationAsync(
            input, Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    [Fact]
    public async Task Transition_WhenRequestTokenCancelled_TagsTheSpanAsClientAborted()
    {
        using var root = new Activity("client-aborted-root").Start();

        await _controller.TransitionAsync(Domain, Flow, Guid.NewGuid().ToString(), "approve", sync: true,
            cancellationToken: CancelledRequestToken());

        root.GetTagItem(TelemetryConstants.TagNames.ClientAborted).ShouldBe(true);
    }

    [Fact]
    public async Task Transition_WhenRunThrowsAfterClientAborted_StillTagsTheSpan()
    {
        using var root = new Activity("client-aborted-throw-root").Start();
        _commandAppService
            .TransitionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TransitionInput>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        await Should.ThrowAsync<InvalidOperationException>(() => _controller.TransitionAsync(
            Domain, Flow, Guid.NewGuid().ToString(), "approve", sync: true,
            cancellationToken: CancelledRequestToken()));

        root.GetTagItem(TelemetryConstants.TagNames.ClientAborted).ShouldBe(true);
    }

    [Fact]
    public async Task Transition_WhenRequestTokenLive_DoesNotTagClientAborted()
    {
        using var root = new Activity("client-live-root").Start();

        await _controller.TransitionAsync(Domain, Flow, Guid.NewGuid().ToString(), "approve", sync: true,
            cancellationToken: CancellationToken.None);

        root.GetTagItem(TelemetryConstants.TagNames.ClientAborted).ShouldBeNull();
    }
}
