using BBT.Aether.Events;
using BBT.Aether.Events.Processing;
using BBT.Workflow.Logging;
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
            logger.OutboxWakeupSignalMissingBody();
            return Ok();
        }

        if (signal.PartitionId < OutboxWakeupSignal.AllPartitions ||
            signal.PartitionId >= outboxOptions.PartitionCount)
        {
            logger.OutboxWakeupSignalPartitionOutOfRange(signal.PartitionId);
            return Ok();
        }

        // Covers the null-schema case too: a payload missing "schema" deserializes signal.Schema
        // as null rather than throwing, and null never equals a configured schema. Logged at
        // Warning, not Debug: per-domain pub/sub plus a subscription scoped to this worker mean a
        // mismatched signal should never arrive in correct operation, so every occurrence means
        // misconfiguration. That makes the noise the point, not something to quiet down.
        if (string.IsNullOrEmpty(signal.Schema) ||
            !string.Equals(signal.Schema, outboxOptions.Schema, StringComparison.Ordinal))
        {
            logger.OutboxWakeupSignalSchemaMismatch(signal.Schema, outboxOptions.Schema);
            return Ok();
        }

        coordinator.Signal(signal.Schema, signal.PartitionId);
        return Ok();
    }
}
