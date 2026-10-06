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
    IDistributedLockService lockService,
    IClock clock,
    ILogger<InstanceScheduleAppService> logger) : IInstanceScheduleAppService
{
    /// <summary>
    /// How long a claimed tick stays claimed. It has to outlive the slowest straggler, not the start:
    /// replica tickers drift, so a late replica can arrive well after the winner has finished. Five
    /// minutes covers that comfortably while still expiring on its own. Each tick has its own key, so
    /// claims never accumulate against one another.
    /// </summary>
    private const int TickClaimSeconds = 300;

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

        var startInput = new StartInstanceInput(input.Domain, input.Workflow, version: null, sync: input.Sync)
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

        // Claim the tick before starting anything. The instance key alone does NOT collapse replicas:
        // the start path's idempotency is a check-then-insert over a non-unique index, so simultaneous
        // callers all pass the probe before any commits (measured: ten parallel calls carrying one
        // tick produced nine instances), and it treats a COMPLETED instance as a free key, so a
        // straggler arriving after a short flow finished creates a second one (measured: two).
        //
        // The claim is taken with the distributed lock's atomic acquire but is deliberately NEVER
        // released — it is a marker, not a scope, and expires on its own. That is what makes it
        // outlive both the start and the instance's own lifetime. Holding a lock across the start
        // instead would be wrong twice over: it would not survive completion, and a sync start (or an
        // `executionType: S` flow, which overrides the caller's request) runs the whole pipeline —
        // tasks, HTTP calls, subflow starts — which must never happen under a held lock.
        var tickKey = $"vnext:schedule:{input.Domain}:{input.Workflow}:{instanceKey}";
        var claim = await lockService.TryAcquireLockAsync(tickKey, TickClaimSeconds, cancellationToken);

        if (claim is null)
        {
            logger.ScheduledStartTickAlreadyInFlight(input.Domain, input.Workflow, instanceKey);
            return Result<object?>.Ok(null);
        }

        activity?.SetTag(TelemetryConstants.TagNames.LockKey, tickKey);
        activity?.SetTag(TelemetryConstants.TagNames.LockAcquired, true);

        // No `await using`: disposing the handle would release the claim and reopen both races above.
        // A failed start keeps the tick claimed on purpose — nothing redelivers a cron tick, and the
        // next tick carries a different key, so there is nothing a release would enable.
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
