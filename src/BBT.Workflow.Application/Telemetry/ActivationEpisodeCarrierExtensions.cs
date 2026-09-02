using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Events;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Telemetry;

/// <summary>
/// Rebuilds the ambient <see cref="ActivationEpisode"/> from the three fields every lane carrier
/// (job payload, lane-aware event) holds beside its anchor, so each consuming entry point is a
/// one-liner. Null when the carrier holds no start — an older producer — which the consumer reports
/// as a partial episode rather than inventing one.
/// </summary>
public static class ActivationEpisodeCarrierExtensions
{
    /// <summary>The episode a job payload carries, or null.</summary>
    public static ActivationEpisode? ToActivationEpisode(this ITraceableJobPayload payload)
        => ActivationEpisode.FromCarrier(payload.EpisodeStartedAt, payload.EpisodeTrigger, payload.EpisodeTransitionKey);

    /// <summary>The episode a lane-aware distributed event carries, or null.</summary>
    public static ActivationEpisode? ToActivationEpisode(this ILaneAwareDistributedEvent evt)
        => ActivationEpisode.FromCarrier(evt.EpisodeStartedAt, evt.EpisodeTrigger, evt.EpisodeTransitionKey);
}
