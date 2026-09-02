using BBT.Aether.Events;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Workers.Outbox.Controllers;

/// <summary>
/// Dapr subscription discovery for the Outbox worker: exactly one subscription — the outbox
/// wakeup nudge. Topic name comes from the same ITopicNameStrategy the publisher uses, so
/// environment prefixing stays consistent by construction.
/// </summary>
[Route("dapr")]
public sealed class DaprSubscribeController(
    ITopicNameStrategy topicNameStrategy,
    AetherEventBusOptions eventBusOptions) : ControllerBase
{
    [HttpGet("subscribe", Order = int.MinValue)]
    [ApiExplorerSettings(IgnoreApi = true)]
    public IActionResult Subscribe()
        => new JsonResult(new[]
        {
            new
            {
                pubsubname = eventBusOptions.PubSubName,
                topic = topicNameStrategy.GetTopicName(typeof(OutboxWakeupEvent)),
                route = "/internal/outbox-wakeup"
            }
        });
}
