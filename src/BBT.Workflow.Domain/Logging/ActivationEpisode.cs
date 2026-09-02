using System.Diagnostics;

namespace BBT.Workflow.Logging;

/// <summary>
/// One <em>activation episode</em>: the interval from the trigger that set an instance in motion
/// (an HTTP transition or start request, a timer firing, an event delivery, a subflow resume) to the
/// instance's next rest point (the Busy→Active flip, a terminal status, or a deliberate rest in
/// Busy). It is the unit a client actually waits for, and the unit the
/// <c>Instance.Activation/{key}</c> span measures.
/// <para>
/// Carried on <see cref="WorkflowTraceLane"/> and, across async boundaries, as three nullable
/// fields (<c>EpisodeStartedAt</c>, <c>EpisodeTrigger</c>, <c>EpisodeTransitionKey</c>) beside the
/// lane anchor in job payloads, outbox events and internal relay bodies. A payload from a build that
/// predates the episode simply yields null on the consuming side; the settling hop then reports a
/// <see cref="Partial"/> episode covering itself alone rather than inventing a start.
/// </para>
/// </summary>
/// <param name="StartedAt">When the trigger was accepted — the server span's start for HTTP entry
/// points, the callback span's start for deferred jobs.</param>
/// <param name="Trigger">What opened the episode; one of <see cref="TelemetryConstants.ActivationTriggers"/>.</param>
/// <param name="TransitionKey">The transition the trigger ran (the first hop's key), when known.</param>
/// <param name="Partial">True when the start was not carried to this hop and covers only the
/// settling hop; excluded from latency aggregates.</param>
public sealed record ActivationEpisode(
    DateTimeOffset StartedAt,
    string Trigger,
    string? TransitionKey,
    bool Partial)
{
    /// <summary>
    /// Opens an episode that starts when <paramref name="activity"/> started (the ambient server or
    /// job span), or now when there is no ambient activity. Not partial: the start is authoritative.
    /// </summary>
    public static ActivationEpisode StartingAt(Activity? activity, string trigger, string? transitionKey = null)
        => new(
            activity is null ? DateTimeOffset.UtcNow : new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero),
            trigger,
            transitionKey,
            Partial: false);

    /// <summary>
    /// Rebuilds an episode from the three carried fields, or null when the carrier holds no start —
    /// the shape every payload, event and relay body shares.
    /// </summary>
    public static ActivationEpisode? FromCarrier(DateTimeOffset? startedAt, string? trigger, string? transitionKey)
        => startedAt is null
            ? null
            : new ActivationEpisode(
                startedAt.Value,
                string.IsNullOrEmpty(trigger) ? TelemetryConstants.ActivationTriggers.Http : trigger,
                transitionKey,
                Partial: false);
}
