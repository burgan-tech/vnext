using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Clock;
using BBT.Aether.DistributedLock;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Schedule;

/// <summary>
/// Turns a scheduler tick into a workflow instance. Registered scoped.
/// </summary>
/// <remarks>
/// Deliberately thin. It derives the instance key, shapes the seed attributes and delegates to
/// <see cref="IInstanceCommandAppService"/>, whose pre-dispatch guard runs the full schema and policy
/// validation — the same delegation <c>EventAppService</c> makes, and for the same reason: the
/// pipeline itself is policy-only, so bypassing the command service would skip start validation.
/// </remarks>
public sealed class InstanceScheduleAppService(
    IInstanceCommandAppService instanceCommandAppService,
    IRuntimeInfoProvider runtimeInfoProvider,
    IInstanceRepository instanceRepository,
    IDistributedLockService lockService,
    IClock clock,
    ILogger<InstanceScheduleAppService> logger) : IInstanceScheduleAppService
{
    /// <summary>
    /// Lease for the per-tick lock. Only has to cover one start. Correctness does not rest on it: if
    /// the lease lapses mid-start the row probe below still rejects the duplicate, because the start
    /// path commits the instance well before the pipeline finishes.
    /// </summary>
    private const int TickLockSeconds = 30;

    /// <inheritdoc />
    public async Task<Result<object?>> StartAsync(
        ScheduledStartInput input,
        CancellationToken cancellationToken = default)
    {
        // A component pointed at the wrong runtime is a permanent misconfiguration, and the answer is
        // a Result rather than a 500: nothing retries a cron tick, and a stack trace every 15 minutes
        // buries the real signal. Asked with the non-throwing IsDomainMatch — Check() exists to throw,
        // and using it here would make an exception the control flow for an expected outcome. It also
        // throws ArgumentException on a blank domain, which would escape as a 500.
        if (!runtimeInfoProvider.IsDomainMatch(input.Domain))
        {
            var reason = $"Invalid domain: \"{input.Domain}\". Expected domain is \"{runtimeInfoProvider.Domain}\".";
            logger.ScheduledStartDomainMismatch(input.Domain, input.Workflow, reason);
            return Result<object?>.Fail(Error.NotFound("ScheduledStartDomainMismatch", reason));
        }

        using var activity = PipelineStepActivityHelper.StartOperationActivity("Schedule.Start");
        activity?.SetTag(TelemetryConstants.TagNames.Domain, input.Domain);
        activity?.SetTag(TelemetryConstants.TagNames.Flow, input.Workflow);

        input.Headers.TryGetValue(ScheduleTickKey.ReadTimeUtcHeader, out var readTimeUtc);

        // No tick header means the caller is not a Dapr cron binding (or Dapr changed the format).
        // Everything still works, but the cross-replica guarantee weakens from "same tick" to "same
        // second on this clock", so say so rather than let it pass unnoticed.
        if (!ScheduleTickKey.TryParseTick(readTimeUtc, out _))
            logger.ScheduledStartTickHeaderMissing(input.Domain, input.Workflow, input.ScheduleId);

        var instanceKey = ScheduleTickKey.Derive(
            input.Workflow, input.ScheduleId, readTimeUtc, clock.UtcNow);

        logger.ScheduledStartReceived(input.Domain, input.Workflow, input.ScheduleId, instanceKey);

        var startInput = new StartInstanceInput(
            input.Domain, input.Workflow, version: input.Version, sync: input.Sync)
        {
            Instance = new CreateInstanceInput
            {
                // The tick-derived key is the whole multi-replica story: every replica firing this
                // tick computes the same value, and the start path's key idempotency returns the
                // instance the first one created instead of making another.
                Key = instanceKey,
                Attributes = BuildAttributes(input.Attributes)
            },
            Headers = new Dictionary<string, string?>(input.Headers)
        };

        // Two different races, two different guards.
        //
        // 1) SIMULTANEOUS replicas. The start path's idempotency is a check-then-insert over a
        //    non-unique index, so concurrent callers all pass the probe before any commits. Measured:
        //    ten parallel calls carrying one tick produced nine instances. The lock serialises them.
        var tickKey = $"vnext:schedule:{input.Domain}:{input.Workflow}:{instanceKey}";
        await using var tickLock = await lockService.TryAcquireLockAsync(
            tickKey, TickLockSeconds, cancellationToken);

        if (tickLock is null)
        {
            logger.ScheduledStartTickAlreadyInFlight(input.Domain, input.Workflow, instanceKey);
            return Result<object?>.Ok(null);
        }

        activity?.SetTag(TelemetryConstants.TagNames.LockKey, tickKey);
        activity?.SetTag(TelemetryConstants.TagNames.LockAcquired, true);

        // 2) A STRAGGLER arriving after the winner released. Serialising is not enough on its own:
        //    the start path treats a COMPLETED instance as a free key
        //    (`if (existingInstance.IsCompleted) return null;`), so a replica whose ticker drifted
        //    past a short flow's completion starts a second instance. Measured: two. This probe is
        //    status-agnostic, which the start path's own probe deliberately is not.
        var existing = await instanceRepository.FindByIdentifierSlimAsync(instanceKey, cancellationToken);
        if (existing is not null)
        {
            logger.ScheduledStartTickAlreadyHandled(input.Domain, input.Workflow, instanceKey);
            return Result<object?>.Ok(null);
        }

        var result = await instanceCommandAppService.StartAsync(startInput, cancellationToken);

        if (!result.IsSuccess)
        {
            logger.ScheduledStartFailed(input.Domain, input.Workflow, instanceKey, result.Error.Message ?? "message not found");
            return Result<object?>.Fail(result.Error);
        }

        return Result<object?>.Ok(result.Value);
    }

    /// <summary>
    /// Turns the surviving query parameters into the new instance's initial attributes. Returns null
    /// for an empty set so a workflow with no start schema is started with no body at all, rather
    /// than with an empty object that a strict schema would reject.
    /// </summary>
    private static JsonElement? BuildAttributes(IReadOnlyDictionary<string, string> attributes)
    {
        if (attributes.Count == 0)
            return null;

        // Values stay strings. A query string carries no types, and silently coercing "1" to a number
        // would make the seed data depend on whether a value happens to look numeric.
        return JsonSerializer.SerializeToElement(attributes);
    }
}
