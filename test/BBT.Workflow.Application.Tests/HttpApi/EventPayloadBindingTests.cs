using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Events;
using BBT.Workflow.Execution.Events;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Related;
using BBT.Workflow.Orchestration.Controllers.Instances;
using BBT.Workflow.SubFlow;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HttpApi;

/// <summary>
/// Guards the content-type-independent body binding of the <c>/instances/events</c> endpoint.
/// Kafka / pub-sub sources routed through Dapr deliver event bodies with no content type or
/// <c>application/octet-stream</c>; the endpoint reads the raw body manually so those are accepted
/// instead of being rejected with 415 by <c>[FromBody]</c> JSON model binding.
/// </summary>
public sealed class EventPayloadBindingTests
{
    private const string PayloadJson = """{"accountNo":"9530","balance":52294471}""";

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    [InlineData(null)]
    public async Task ReadEventPayload_ParsesJson_RegardlessOfContentType(string? contentType)
    {
        var request = BuildRequest(PayloadJson, contentType);

        var payload = await InstanceController.ReadEventPayloadAsync(request, CancellationToken.None);

        payload.ValueKind.ShouldBe(JsonValueKind.Object);
        payload.GetProperty("accountNo").GetString().ShouldBe("9530");
        payload.GetProperty("balance").GetInt64().ShouldBe(52294471);
    }

    [Fact]
    public async Task ReadEventPayload_EmptyBody_ReturnsUndefined()
    {
        var request = BuildRequest(string.Empty, "application/octet-stream");

        var payload = await InstanceController.ReadEventPayloadAsync(request, CancellationToken.None);

        payload.ValueKind.ShouldBe(JsonValueKind.Undefined);
    }

    [Fact]
    public async Task ReadEventPayload_MalformedJson_ThrowsJsonException()
    {
        var request = BuildRequest("{ not-json", "application/octet-stream");

        await Should.ThrowAsync<JsonException>(() =>
            InstanceController.ReadEventPayloadAsync(request, CancellationToken.None));
    }

    /// <summary>
    /// A body that is not JSON, or a subscription routed at an action the endpoint does not know, can
    /// never be processed. Both must be answered with Dapr's <c>DROP</c> signal rather than a 4xx —
    /// Dapr redelivers non-2xx responses, so a single such message would block the topic partition.
    /// </summary>
    [Theory]
    [InlineData("{ not-json", "start", "InvalidEventPayload")]
    [InlineData(PayloadJson, "strat", "InvalidEventAction")]
    public async Task HandleEvent_PermanentlyUnprocessableDelivery_ReturnsDropSignal(
        string body, string action, string expectedCode)
    {
        var controller = BuildController(body);

        var actionResult = await controller.HandleEventAsync(
            domain: "orders",
            workflow: "order-flow",
            action: action,
            cancellationToken: CancellationToken.None);

        var objectResult = actionResult.ShouldBeAssignableTo<ObjectResult>()!;
        objectResult.StatusCode.ShouldBe(StatusCodes.Status200OK);

        var response = objectResult.Value.ShouldBeOfType<EventDeliveryResponse>();
        response.Status.ShouldBe(DaprPubSubStatus.Drop);
        response.Reason!.ShouldContain(expectedCode);
    }

    [Theory]
    [InlineData("not-an-actor")]
    [InlineData("999")]
    public async Task EnqueueTransition_InvalidExecutionActor_ReturnsBadRequestWithoutEnqueueing(
        string actor)
    {
        var enqueuer = Substitute.For<ITransitionJobEnqueuer>();
        var controller = BuildController(string.Empty, enqueuer);
        var instanceId = Guid.NewGuid();
        var continuation = new TransitionContinuationRequested
        {
            JobId = Guid.NewGuid(),
            JobName = "job",
            InstanceId = instanceId,
            Domain = "orders",
            Flow = "order-flow",
            Version = "1.0.0",
            TransitionKey = "approve",
            ExecutionActor = actor
        };

        var result = await controller.EnqueueTransitionAsync(
            continuation.Domain,
            continuation.Flow,
            continuation.InstanceId,
            continuation.TransitionKey,
            continuation,
            CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
        await enqueuer.DidNotReceive().EnqueueAsync(
            Arg.Any<TransitionJobPayload>(),
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(999)]
    public async Task EnqueueTransition_InvalidTriggerType_ReturnsBadRequestWithoutEnqueueing(
        int triggerType)
    {
        var enqueuer = Substitute.For<ITransitionJobEnqueuer>();
        var controller = BuildController(string.Empty, enqueuer);
        var continuation = CreateContinuation();
        continuation = new TransitionContinuationRequested
        {
            JobId = continuation.JobId,
            JobName = continuation.JobName,
            InstanceId = continuation.InstanceId,
            Domain = continuation.Domain,
            Flow = continuation.Flow,
            Version = continuation.Version,
            TransitionKey = continuation.TransitionKey,
            ExecutionActor = continuation.ExecutionActor,
            TriggerType = triggerType
        };

        var result = await controller.EnqueueTransitionAsync(
            continuation.Domain,
            continuation.Flow,
            continuation.InstanceId,
            continuation.TransitionKey,
            continuation,
            CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
        await enqueuer.DidNotReceive().EnqueueAsync(
            Arg.Any<TransitionJobPayload>(),
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnqueueTransition_ValidDelivery_FiltersSensitiveHeadersBeforeScheduling()
    {
        var enqueuer = Substitute.For<ITransitionJobEnqueuer>();
        TransitionJobPayload? captured = null;
        await enqueuer.EnqueueAsync(
            Arg.Do<TransitionJobPayload>(payload => captured = payload),
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
        var controller = BuildController(string.Empty, enqueuer);
        var continuation = CreateContinuation(new Dictionary<string, string?>
        {
            ["Authorization"] = "Bearer secret",
            ["x-flow-context"] = "keep"
        });

        var result = await controller.EnqueueTransitionAsync(
            continuation.Domain,
            continuation.Flow,
            continuation.InstanceId,
            continuation.TransitionKey,
            continuation,
            CancellationToken.None);

        result.ShouldBeOfType<OkResult>();
        captured.ShouldNotBeNull();
        captured.Headers.ShouldNotContainKey("Authorization");
        captured.Headers["x-flow-context"].ShouldBe("keep");
        captured.TriggerType.ShouldBe(BBT.Workflow.Definitions.TriggerType.Automatic);
    }

    private static TransitionContinuationRequested CreateContinuation(
        Dictionary<string, string?>? headers = null)
        => new()
        {
            JobId = Guid.NewGuid(),
            JobName = "job",
            InstanceId = Guid.NewGuid(),
            Domain = "orders",
            Flow = "order-flow",
            Version = "1.0.0",
            TransitionKey = "approve",
            ExecutionActor = "System",
            TriggerType = (int)BBT.Workflow.Definitions.TriggerType.Automatic,
            Headers = headers ?? new Dictionary<string, string?>()
        };

    /// <summary>
    /// Builds a controller wired only far enough to reach the two pre-service guards; the event app
    /// service is never invoked on those paths.
    /// </summary>
    private static InstanceController BuildController(
        string body,
        ITransitionJobEnqueuer? transitionJobEnqueuer = null)
    {
        var controller = new InstanceController(
            commandAppService: Substitute.For<IInstanceCommandAppService>(),
            queryAppService: Substitute.For<IInstanceQueryAppService>(),
            retryAppService: Substitute.For<IInstanceRetryAppService>(),
            httpContextAccessor: Substitute.For<IHttpContextAccessor>(),
            subflowCompletionService: Substitute.For<ISubflowCompletionService>(),
            subflowStateService: Substitute.For<ISubflowStateService>(),
            subflowFaultService: Substitute.For<ISubflowFaultService>(),
            subflowCancellationService: Substitute.For<ISubflowCancellationService>(),
            cancellationService: Substitute.For<IInstanceCancellationService>(),
            childSubflowCancellationService: Substitute.For<IChildSubflowCancellationService>(),
            childSubflowFaultService: Substitute.For<IChildSubflowFaultService>(),
            transitionJobEnqueuer: transitionJobEnqueuer ?? Substitute.For<ITransitionJobEnqueuer>(),
            instanceCommandGateway: Substitute.For<IInstanceCommandGateway>(),
            eventAppService: Substitute.For<IEventAppService>(),
            relatedInstanceQueryAppService: Substitute.For<IRelatedInstanceQueryAppService>(),
            currentUser: Substitute.For<BBT.Aether.Users.ICurrentUser>());

        // AetherControllerBase resolves its Logger through the request services.
        var services = new ServiceCollection();
        services.AddSingleton<ILazyServiceProvider, LazyServiceProvider>();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

        var context = BuildRequest(body, "application/octet-stream").HttpContext;
        context.RequestServices = services.BuildServiceProvider();
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        return controller;
    }

    private static HttpRequest BuildRequest(string body, string? contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        if (contentType is not null)
            context.Request.ContentType = contentType;

        return context.Request;
    }
}
