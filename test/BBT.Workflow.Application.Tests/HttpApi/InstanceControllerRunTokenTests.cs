using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Results;
using BBT.Workflow.Application.Tests.Execution.Cancellation;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.Events;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
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

        var services = new ServiceCollection();
        services.AddSingleton<ILazyServiceProvider, LazyServiceProvider>();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);

        _controller = new InstanceController(
            commandAppService: _commandAppService,
            queryAppService: Substitute.For<IInstanceQueryAppService>(),
            retryAppService: Substitute.For<IInstanceRetryAppService>(),
            httpContextAccessor: accessor,
            subflowCompletionService: Substitute.For<ISubflowCompletionService>(),
            subflowStateService: Substitute.For<ISubflowStateService>(),
            subflowFaultService: Substitute.For<ISubflowFaultService>(),
            subflowCancellationService: Substitute.For<ISubflowCancellationService>(),
            cancellationService: Substitute.For<IInstanceCancellationService>(),
            childSubflowCancellationService: Substitute.For<IChildSubflowCancellationService>(),
            childSubflowFaultService: Substitute.For<IChildSubflowFaultService>(),
            transitionJobEnqueuer: Substitute.For<ITransitionJobEnqueuer>(),
            instanceCommandGateway: Substitute.For<IInstanceCommandGateway>(),
            eventAppService: Substitute.For<IEventAppService>(),
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
