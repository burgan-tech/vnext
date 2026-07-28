using BBT.Aether.Events;
using BBT.Aether.Events.Processing;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Workers.Outbox.Controllers;

/// <summary>
/// Receives outbox wake-up signals from Dapr pub/sub.
/// </summary>
/// <remarks>
/// Does nothing but nudge the in-memory <see cref="IOutboxSignalCoordinator"/> and return. No
/// database query, no publish, no retry loop: the endpoint must return promptly so the broker's
/// retry behaviour is not coupled to dispatch processing. A signal is a hint — dropping one only
/// costs latency, because fallback polling still finds the rows.
/// </remarks>
[ApiController]
public sealed class OutboxWakeupController(
    IOutboxSignalCoordinator coordinator,
    AetherOutboxOptions outboxOptions,
    ILogger<OutboxWakeupController> logger) : ControllerBase
{
    /// <summary>
    /// Handles a wake-up signal. Always returns 200 so the broker does not redeliver — a rejected
    /// signal gains nothing, since fallback polling reconciles regardless.
    /// </summary>
    /// <param name="signal">
    /// The wake-up hint, or <c>null</c> when the body was empty or missing the "schema" field.
    /// </param>
    [HttpPost("/internal/outbox/wakeup")]
    public IActionResult Wakeup([FromBody] OutboxWakeupSignal? signal)
    {
        if (signal is null)
        {
            logger.LogWarning("Outbox wake-up signal had no body; ignoring.");
            return Ok();
        }

        if (signal.PartitionId < OutboxWakeupSignal.AllPartitions ||
            signal.PartitionId >= outboxOptions.PartitionCount)
        {
            logger.LogWarning(
                "Outbox wake-up signal had out-of-range PartitionId {PartitionId}; ignoring.",
                signal.PartitionId);
            return Ok();
        }

        // Covers the null-schema case too: a payload missing "schema" deserializes signal.Schema
        // as null rather than throwing, and null never equals a configured schema.
        if (string.IsNullOrEmpty(signal.Schema) ||
            !string.Equals(signal.Schema, outboxOptions.Schema, StringComparison.Ordinal))
        {
            logger.LogDebug(
                "Outbox wake-up signal for schema {SignalSchema} ignored; this worker serves {WorkerSchema}.",
                signal.Schema, outboxOptions.Schema);
            return Ok();
        }

        coordinator.Signal(signal.Schema, signal.PartitionId);
        return Ok();
    }
}
