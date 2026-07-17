using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.Events;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Instances.Remote;
using BBT.Workflow.Orchestration.Controllers.Instances;
using BBT.Workflow.SubFlow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Remote;

public sealed class InstanceControllerTransitionEnvelopeTests
{
    [Fact]
    public async Task TransitionAsync_WithInternalEnvelopeHeader_ReconstructsDataAndTermination()
    {
        var initiator = Guid.NewGuid();
        var cascade = Guid.NewGuid();
        using var bodyDocument = JsonDocument.Parse($$"""
        {
          "data": { "key": "cancel-key", "attributes": { "reason": "parent-cascade" } },
          "termination": {
            "origin": "ParentCascade",
            "initiatorInstanceId": "{{initiator}}",
            "cascadeId": "{{cascade}}"
          }
        }
        """);
        var commandService = Substitute.For<IInstanceCommandAppService>();
        TransitionInput? captured = null;
        commandService.TransitionAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Do<TransitionInput>(value => captured = value),
                Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput()));
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[InternalTransitionEnvelope.HeaderName] =
            InternalTransitionEnvelope.HeaderValue;
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);
        var sut = CreateController(commandService, accessor);
        sut.ControllerContext = new ControllerContext { HttpContext = httpContext };

        await sut.TransitionAsync(
            "parent-domain",
            "parent-flow",
            Guid.NewGuid().ToString(),
            "cancel",
            bodyDocument.RootElement.Clone(),
            cancellationToken: CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.Data.ShouldNotBeNull();
        captured.Data.Key.ShouldBe("cancel-key");
        captured.Data.Attributes!.Value.GetProperty("reason").GetString().ShouldBe("parent-cascade");
        captured.Termination.ShouldNotBeNull();
        captured.Termination.Origin.ShouldBe(TerminationOrigin.ParentCascade);
        captured.Termination.InitiatorInstanceId.ShouldBe(initiator);
        captured.Termination.CascadeId.ShouldBe(cascade);
    }

    private static InstanceController CreateController(
        IInstanceCommandAppService commandService,
        IHttpContextAccessor accessor) => new(
        commandService,
        Substitute.For<IInstanceQueryAppService>(),
        Substitute.For<IInstanceRetryAppService>(),
        accessor,
        Substitute.For<ISubflowCompletionService>(),
        Substitute.For<ISubflowStateService>(),
        Substitute.For<ISubflowFaultService>(),
        Substitute.For<ISubflowCancellationService>(),
        Substitute.For<IInstanceCancellationService>(),
        Substitute.For<IChildSubflowCancellationService>(),
        Substitute.For<IChildSubflowFaultService>(),
        Substitute.For<ITransitionJobEnqueuer>(),
        Substitute.For<IInstanceCommandGateway>(),
        Substitute.For<IEventAppService>());
}
