using BBT.Aether.Events;
using BBT.Aether.Polling;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Workers.Outbox.Controllers;

/// <summary>
/// Receives the <see cref="OutboxWakeupEvent"/> nudge from pub/sub and wakes the outbox poller
/// immediately. Deliberately NOT inbox-backed: the nudge is loss-tolerant (polling backstops it)
/// and duplicate-tolerant (signals coalesce), so durability machinery would only add latency.
/// </summary>
[ApiController]
public sealed class OutboxWakeupController(
    IPollingWakeSignal<IOutboxProcessor> wakeSignal) : ControllerBase
{
    [HttpPost("internal/outbox-wakeup")]
    public IActionResult Wake()
    {
        wakeSignal.Signal();
        return Ok();
    }
}
