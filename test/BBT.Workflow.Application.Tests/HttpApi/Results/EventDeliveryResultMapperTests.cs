using System;
using System.Text.Json;
using BBT.Aether.AspNetCore.ExceptionHandling;
using BBT.Aether.Results;
using BBT.Workflow.Definitions.Events;
using BBT.Workflow.Events;
using BBT.Workflow.Instances;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Orchestration.Controllers.Instances;

/// <summary>
/// Guards the Dapr pub/sub response contract of the <c>/instances/events</c> endpoint.
/// Dapr reads the top-level <c>status</c> field of the response body as its delivery signal, so a body
/// carrying an <c>InstanceStatus</c> code there (<c>"B"</c>, <c>"A"</c>, …) makes Dapr treat every
/// delivery as failed and redeliver the same message forever, blocking the topic partition.
/// </summary>
public sealed class EventDeliveryResultMapperTests
{
    [Fact]
    public void ToActionResult_Success_ReturnsDaprSuccessSignal()
    {
        var actionResult = Map(SuccessResult(), Input(sync: false));

        var body = ShouldBeOkWith(actionResult);
        body.Status.ShouldBe(DaprPubSubStatus.Success);
    }

    [Fact]
    public void ToActionResult_SuccessWithoutSync_OmitsInstanceSnapshot()
    {
        var actionResult = Map(SuccessResult(), Input(sync: false));

        ShouldBeOkWith(actionResult).Instance.ShouldBeNull();
        Serialize(actionResult).TryGetProperty("instance", out _).ShouldBeFalse();
    }

    [Fact]
    public void ToActionResult_SuccessWithSync_NestsInstanceSnapshot()
    {
        var actionResult = Map(SuccessResult(key: "instance-1"), Input(sync: true));

        var body = ShouldBeOkWith(actionResult);
        body.Instance.ShouldNotBeNull();
        body.Instance!.Key.ShouldBe("instance-1");
        // The instance status code is safe here because Dapr only reads the top-level status.
        body.Instance.Status.ShouldBe(InstanceStatus.Busy.Code);
    }

    [Fact]
    public void ToActionResult_SuccessWithNullValue_ReturnsOkSuccessNotNoContent()
    {
        // "No active instance matched" is a deliberate acknowledgement — the event must evaporate,
        // not be redelivered. Previously this mapped to 204 via Aether's null-value convention.
        var actionResult = Map(Result<object?>.Ok(null), Input(sync: false));

        var objectResult = actionResult.ShouldBeAssignableTo<ObjectResult>()!;
        objectResult.StatusCode.ShouldBe(StatusCodes.Status200OK);
        ShouldBeOkWith(actionResult).Status.ShouldBe(DaprPubSubStatus.Success);
    }

    /// <summary>
    /// Serialized guard for the original defect: the body Dapr parses must carry a protocol signal,
    /// never an instance status code.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToActionResult_Success_SerializedStatusIsProtocolSignal(bool sync)
    {
        var json = Serialize(Map(SuccessResult(), Input(sync)));

        json.GetProperty("status").GetString().ShouldBe("SUCCESS");
        json.GetProperty("status").GetString().ShouldNotBe(InstanceStatus.Busy.Code);
    }

    [Fact]
    public void ToActionResult_ValidationError_DropsInsteadOfRetrying()
    {
        var result = Result<object?>.Fail(Error.Validation(
            "NotAnEventTransition", "Transition 'abort' is not an event transition."));

        var body = ShouldBeOkWith(Map(result, Input(sync: false)));

        body.Status.ShouldBe(DaprPubSubStatus.Drop);
        body.Reason!.ShouldContain("NotAnEventTransition");
        body.Reason!.ShouldContain("is not an event transition");
    }

    [Fact]
    public void ToActionResult_NotFoundError_DropsInsteadOfRetrying()
    {
        var result = Result<object?>.Fail(Error.NotFound(
            "TransitionNotFound", "Transition 'typo' not found in workflow 'orders'."));

        var body = ShouldBeOkWith(Map(result, Input(sync: false)));

        body.Status.ShouldBe(DaprPubSubStatus.Drop);
        body.Reason!.ShouldContain("TransitionNotFound");
    }

    [Fact]
    public void Drop_ReturnsOkDropSignalWithReason()
    {
        var actionResult = EventDeliveryResultMapper.Drop(
            "InvalidEventAction: action must be 'start' or 'transition'. Received: 'strat'.",
            "orders", "order-flow", null, "InvalidEventAction", NullLogger.Instance);

        var objectResult = actionResult.ShouldBeAssignableTo<ObjectResult>()!;
        objectResult.StatusCode.ShouldBe(StatusCodes.Status200OK);

        var body = objectResult.Value.ShouldBeOfType<EventDeliveryResponse>();
        body.Status.ShouldBe(DaprPubSubStatus.Drop);
        body.Reason!.ShouldContain("InvalidEventAction");
    }

    [Fact]
    public void ToActionResult_TransientFailure_KeepsNonSuccessStatusForRedelivery()
    {
        // Infrastructure failures should still be retried by the broker, so they keep the
        // ProblemDetails response rather than being acknowledged with a 200 signal.
        var result = Result<object?>.Fail(Error.Failure("EventMappingFailed", "Redis timeout."));

        var actionResult = Map(result, Input(sync: false));

        var objectResult = actionResult.ShouldBeAssignableTo<ObjectResult>()!;
        objectResult.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        objectResult.Value.ShouldNotBeOfType<EventDeliveryResponse>();
    }

    private static IActionResult Map(Result<object?> result, EventInput input)
        => EventDeliveryResultMapper.ToActionResult(result, input, BuildHttpContext(), NullLogger.Instance);

    private static Result<object?> SuccessResult(string? key = null)
        => Result<object?>.Ok(new StartInstanceOutput
        {
            Id = Guid.NewGuid(),
            Key = key,
            Status = InstanceStatus.Busy
        });

    private static EventInput Input(bool sync) => new()
    {
        Domain = "orders",
        Workflow = "order-flow",
        Action = EventAction.Start,
        Sync = sync
    };

    private static EventDeliveryResponse ShouldBeOkWith(IActionResult actionResult)
    {
        var objectResult = actionResult.ShouldBeAssignableTo<ObjectResult>()!;
        objectResult.StatusCode.ShouldBe(StatusCodes.Status200OK);
        return objectResult.Value.ShouldBeOfType<EventDeliveryResponse>();
    }

    private static JsonElement Serialize(IActionResult actionResult)
    {
        var value = actionResult.ShouldBeAssignableTo<ObjectResult>()!.Value;
        return JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
    }

    /// <summary>
    /// Aether's failure path resolves <see cref="IProblemDetailsFactory"/> from request services, so
    /// the transient-failure passthrough needs a container.
    /// </summary>
    private static HttpContext BuildHttpContext()
    {
        var factory = Substitute.For<IProblemDetailsFactory>();
        factory.CreateProblemDetails(Arg.Any<Error>(), Arg.Any<HttpContext>())
            .Returns(callInfo => new ProblemDetails
            {
                Title = callInfo.Arg<Error>().Code,
                Status = StatusCodes.Status500InternalServerError
            });

        var services = new ServiceCollection();
        services.AddSingleton(factory);

        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }
}
