using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;

namespace BBT.Workflow.Schedule;

/// <summary>
/// Starts a workflow instance because a schedule fired.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <c>IEventAppService</c> on purpose. An event delivery carries a message that a
/// domain-authored mapping script must interpret — correlate it to an instance, shape a body. A
/// scheduled tick carries no message: it says only "the time arrived". Routing it through the event
/// path would force every schedulable workflow to ship an <c>event.mapping</c> script whose sole job
/// is to invent a key, which is friction for no gain.
/// </para>
/// <para>
/// The delivery infrastructure stays outside the runtime, exactly as it does for events: the runtime
/// exposes this entry point and knows nothing about cron expressions, timezones or how many
/// schedules exist. A schedule is one more YAML owned by the domain.
/// </para>
/// </remarks>
public interface IInstanceScheduleAppService
{
    /// <summary>
    /// Starts an instance for one tick.
    /// </summary>
    /// <param name="input">The tick: target workflow, schedule identity, seed attributes, headers.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// The started instance, or the one an earlier replica already started for the same tick — the
    /// start path's key idempotency makes those indistinguishable to the caller, which is the point.
    /// </returns>
    Task<Result<object?>> StartAsync(ScheduledStartInput input, CancellationToken cancellationToken = default);
}
