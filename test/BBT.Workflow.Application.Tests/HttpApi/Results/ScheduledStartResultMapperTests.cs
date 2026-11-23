using BBT.Aether.Results;
using BBT.Workflow.Events;
using BBT.Workflow.Schedule;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Orchestration.Controllers.Instances;

/// <summary>
/// The DROP body of <c>/instances/schedule</c> carries only the error code; the full message stays
/// in the EventId 40994 log.
/// </summary>
public sealed class ScheduledStartResultMapperTests
{
    [Fact]
    public void ToActionResult_NotFound_DropsWithCodeOnly()
    {
        var error = Error.NotFound("WorkflowNotFound", "Workflow 'north-star' not found in domain 'orders'.");
        var logger = new EventDeliveryResultMapperTests.CapturingLogger();
        var input = new ScheduledStartInput { Domain = "orders", Workflow = "north-star" };

        var actionResult = ScheduledStartResultMapper.ToActionResult(
            Result<object?>.Fail(error), input, new DefaultHttpContext(), logger);

        var objectResult = actionResult.ShouldBeAssignableTo<ObjectResult>()!;
        objectResult.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var body = objectResult.Value.ShouldBeOfType<EventDeliveryResponse>();
        body.Status.ShouldBe(DaprPubSubStatus.Drop);
        body.Reason.ShouldBe(error.Code);
        body.Reason!.ShouldNotContain("north-star");
        logger.Entries.ShouldContain(e => e.EventId == 40994
            && e.Message.Contains("north-star") && e.Message.Contains("WorkflowNotFound"));
    }
}
