using System;
using System.Collections.Generic;
using BBT.Aether.Results;
using BBT.Workflow.Events;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.DTOs;
using BBT.Workflow.Orchestration.Controllers.Instances;
using BBT.Workflow.Schedule;
using Microsoft.AspNetCore.Http;
using BBT.Aether.AspNetCore.ExceptionHandling;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Schedule;

/// <summary>
/// Covers how a scheduled start's outcome is turned into a Dapr-shaped answer.
/// <para>
/// This mapper decides what an operator sees. Nothing redelivers a cron tick, so the
/// <c>DROP</c>-versus-non-2xx split cannot change the fate of the occurrence — it changes whether a
/// recurring misconfiguration reads as a configuration defect or as an outage.
/// </para>
/// </summary>
public sealed class ScheduledStartResultMapperTests
{
    /// <summary>
    /// The transient passthrough asks the request's factory for a body; a bare substitute would hand
    /// back null and fail inside the mapper rather than at the assertion.
    /// </summary>
    private static IProblemDetailsFactory ProblemDetailsFactory()
    {
        var factory = Substitute.For<IProblemDetailsFactory>();
        factory.CreateProblemDetails(Arg.Any<Error>(), Arg.Any<HttpContext>())
            .Returns(new ProblemDetails { Status = 500, Title = "Transient" });
        return factory;
    }

    private static ScheduledStartInput Tick(bool sync = false) => new()
    {
        Domain = "morph-touch",
        Workflow = "rezervation",
        ScheduleId = "daily",
        Sync = sync,
        Headers = new Dictionary<string, string?>()
    };

    private static IActionResult Map(Result<object?> result, bool sync = false)
    {
        // The transient passthrough resolves services off the request, so a bare DefaultHttpContext
        // (RequestServices == null) throws before reaching the branch under test.
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton(ProblemDetailsFactory())
                .BuildServiceProvider()
        };

        return ScheduledStartResultMapper.ToActionResult(
            result, Tick(sync), httpContext, NullLogger.Instance);
    }

    /// <summary>
    /// An instance DTO must never reach a Dapr caller: its <c>status</c> serialises to an
    /// <see cref="InstanceStatus"/> code, which Dapr reads as an unrecognised protocol signal and
    /// then redelivers forever.
    /// </summary>
    [Fact]
    public void Success_answers_with_the_dapr_protocol_body()
    {
        var action = Map(Result<object?>.Ok(null)).ShouldBeOfType<OkObjectResult>();

        var body = action.Value.ShouldBeOfType<EventDeliveryResponse>();
        body.Status.ShouldBe(DaprPubSubStatus.Success);
    }

    /// <summary>Identity is echoed only to a caller that asked to block for it.</summary>
    [Fact]
    public void Instance_identity_is_echoed_only_for_sync_callers()
    {
        var started = new StartInstanceOutput { Id = Guid.NewGuid(), Key = "daily-20261005T072542Z" };

        var asyncBody = Map(Result<object?>.Ok(started))
            .ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<EventDeliveryResponse>();
        var syncBody = Map(Result<object?>.Ok(started), sync: true)
            .ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<EventDeliveryResponse>();

        asyncBody.Instance.ShouldBeNull();
        syncBody.Instance.ShouldNotBeNull();
        syncBody.Instance!.Key.ShouldBe("daily-20261005T072542Z");
    }

    /// <summary>
    /// A failure a later tick could never fix is answered <c>200 DROP</c> with the reason, so the
    /// discarded occurrence stays diagnosable even though the status code says success.
    /// </summary>
    [Theory]
    [InlineData("Validation")]
    [InlineData("NotFound")]
    [InlineData("NotSupported")]
    [InlineData("Unauthorized")]
    [InlineData("Forbidden")]
    public void Permanent_failures_are_dropped_with_a_reason(string prefix)
    {
        var error = prefix switch
        {
            "Validation" => Error.Validation("App:900002", "JSON schema validation failed"),
            "NotFound" => Error.NotFound("Cache:300001", "Workflow not found"),
            "NotSupported" => Error.NotSupported("X:1", "nope"),
            "Unauthorized" => Error.Unauthorized("X:2", "nope"),
            _ => Error.Forbidden("X:3", "nope")
        };

        var body = Map(Result<object?>.Fail(error))
            .ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<EventDeliveryResponse>();

        body.Status.ShouldBe(DaprPubSubStatus.Drop);
        body.Reason.ShouldNotBeNullOrWhiteSpace();
        body.Reason!.ShouldContain(error.Code);
    }

    /// <summary>
    /// A transient failure keeps the problem-details response. Non-2xx is the honest answer even
    /// though no cron tick is ever redelivered.
    /// </summary>
    [Fact]
    public void Transient_failures_are_not_dropped()
    {
        var action = Map(Result<object?>.Fail(Error.Dependency("Dep:1", "redis down", "Redis")));

        action.ShouldNotBeOfType<OkObjectResult>();
    }
}
