using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.Events;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Instances.Related;
using BBT.Workflow.Logging;
using BBT.Workflow.Orchestration.Controllers.Instances;
using BBT.Workflow.Scripting.Related;
using BBT.Workflow.SubFlow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Shouldly;
using Xunit;
using BBT.Workflow.Authorization;

namespace BBT.Workflow.Infrastructure.Tests.Remote;

public sealed class InstanceControllerChildCancelTests
{
    private const string RemovedEnvelopeHeader = "X-Vnext-Internal-Transition-Envelope";

    [Fact]
    public async Task StartSubAsync_AdoptsCarriedEpisodeWithoutLeakingScope()
    {
        var startedAt = new DateTimeOffset(2026, 9, 2, 12, 34, 56, TimeSpan.Zero);
        const string requestAnchor = "00-11111111111111111111111111111111-2222222222222222-01";
        var requestEpisode = new ActivationEpisode(
            startedAt.AddSeconds(1),
            TelemetryConstants.ActivationTriggers.Http,
            null,
            Partial: false);
        ActivationEpisode? observedEpisode = null;
        string? observedAnchor = null;
        var commandService = Substitute.For<IInstanceCommandAppService>();
        commandService.StartAsync(
                Arg.Do<StartInstanceInput>(_ =>
                {
                    observedEpisode = WorkflowTraceLane.Episode;
                    observedAnchor = WorkflowTraceLane.Current;
                }),
                Arg.Any<CancellationToken>())
            .Returns(Result<StartInstanceOutput>.Ok(new StartInstanceOutput()));
        var httpContext = new DefaultHttpContext();
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);
        var sut = CreateController(commandService, accessor, Substitute.For<IChildSubflowCancellationService>());
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        var request = new CreateSubInstanceDto
        {
            ExtraProperties = new Dictionary<string, object?>(),
            EpisodeStartedAt = startedAt,
            EpisodeTrigger = TelemetryConstants.ActivationTriggers.Manual,
            EpisodeTransitionKey = "approve"
        };

        using (WorkflowTraceLane.Use(requestAnchor, episode: requestEpisode))
        {
            await sut.StartSubAsync(
                "child-domain",
                "child-flow",
                request,
                cancellationToken: CancellationToken.None);

            WorkflowTraceLane.Current.ShouldBe(requestAnchor);
            WorkflowTraceLane.Episode.ShouldBe(requestEpisode);
        }

        observedAnchor.ShouldBe(requestAnchor);
        observedEpisode.ShouldBe(new ActivationEpisode(
            startedAt,
            TelemetryConstants.ActivationTriggers.Manual,
            "approve",
            Partial: false));
        WorkflowTraceLane.Episode.ShouldBeNull();
    }

    [Fact]
    public async Task TransitionAsync_WithRemovedInternalMarker_TreatsBodyAsNormalRawPayload()
    {
        var commandService = Substitute.For<IInstanceCommandAppService>();
        TransitionInput? captured = null;
        commandService.TransitionAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<TransitionInput>(value => captured = value),
                Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput()));
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[RemovedEnvelopeHeader] = "1";
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);
        var sut = CreateController(commandService, accessor, Substitute.For<IChildSubflowCancellationService>());
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        using var body = JsonDocument.Parse("""
        {
          "data": { "key": "cancel-key" },
          "termination": {
            "origin": "ParentCascade",
            "initiatorInstanceId": "11111111-1111-1111-1111-111111111111",
            "cascadeId": "22222222-2222-2222-2222-222222222222"
          }
        }
        """);

        await sut.TransitionAsync(
            "child-domain", "child-flow", Guid.NewGuid().ToString(), "cancel",
            body.RootElement.Clone(), cancellationToken: CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.Termination.ShouldBeNull();
        captured.Data.ShouldNotBeNull();
        captured.Data.Attributes!.Value.GetProperty("termination")
            .GetProperty("origin").GetString().ShouldBe("ParentCascade");
    }

    [Fact]
    public async Task ChildCancelAsync_DeserializesTypedBodyAndPreservesCascadeIdentity()
    {
        var initiator = Guid.NewGuid();
        var cascade = Guid.NewGuid();
        var childService = Substitute.For<IChildSubflowCancellationService>();
        childService.CancelChildSubflowAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<TerminationContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());
        using var json = JsonDocument.Parse($$"""
        {
          "version": "1.2.3",
          "termination": {
            "origin": "parentCascade",
            "initiatorInstanceId": "{{initiator}}",
            "cascadeId": "{{cascade}}"
          }
        }
        """);
        var request = JsonSerializer.Deserialize<ChildSubflowCancelInput>(
            json.RootElement,
            JsonSerializerConstants.JsonOptions)!;
        var httpContext = new DefaultHttpContext();
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);
        var sut = CreateController(Substitute.For<IInstanceCommandAppService>(), accessor, childService);
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };
        var instanceId = Guid.NewGuid();

        await sut.ChildCancelAsync(
            "child-domain", "child-flow", instanceId, request, CancellationToken.None);

        await childService.Received(1).CancelChildSubflowAsync(
            instanceId,
            "child-domain",
            "child-flow",
            "1.2.3",
            Arg.Is<TerminationContext>(value =>
                value.Origin == TerminationOrigin.ParentCascade &&
                value.InitiatorInstanceId == initiator &&
                value.CascadeId == cascade),
            CancellationToken.None);
    }

    private static InstanceController CreateController(
        IInstanceCommandAppService commandService,
        IHttpContextAccessor accessor,
        IChildSubflowCancellationService childService,
        IRelatedInstanceQueryAppService? relatedInstanceQueryAppService = null) => new(
        commandService,
        Substitute.For<IInstanceQueryAppService>(),
        Substitute.For<IInstanceRetryAppService>(),
        accessor,
        Substitute.For<ISubflowCompletionService>(),
        Substitute.For<ISubflowStateService>(),
        Substitute.For<ISubflowFaultService>(),
        Substitute.For<ISubflowCancellationService>(),
        Substitute.For<IInstanceCancellationService>(),
        childService,
        Substitute.For<IChildSubflowFaultService>(),
        Substitute.For<ITransitionJobEnqueuer>(),
        Substitute.For<IInstanceCommandGateway>(),
        Substitute.For<IEventAppService>(),
        relatedInstanceQueryAppService ?? Substitute.For<IRelatedInstanceQueryAppService>(),
        new DefaultCallerRoleResolver(Substitute.For<ICurrentUser>()));
}
