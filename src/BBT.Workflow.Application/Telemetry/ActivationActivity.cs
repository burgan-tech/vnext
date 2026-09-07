using System.Diagnostics;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Telemetry;

/// <summary>
/// Emits the <c>Instance.Activation/{key}</c> span: one span per <em>activation episode</em>, from the
/// trigger that set the instance in motion to the rest point the client can observe (Active,
/// Completed/Canceled, Faulted, or a deliberate rest in Busy). Its duration is the number the
/// "how long until the flow was available" question asks for, and its tags say how the episode ended.
/// <para>
/// <b>Why it is synthetic and backdated.</b> An <see cref="Activity"/>'s span id is minted at
/// <c>Start()</c>, so no span can both be known at accept time (to be the real parent of the hops
/// that follow) and be stopped at the rest point, which usually happens in another process, after a
/// Dapr scheduler round-trip. The hops are therefore parented to the lane anchor (the APM
/// transaction) as they always were, and this span is created at the rest point with
/// <c>startTime</c> = the carried episode start — a sibling of the hops under the same anchor,
/// covering all of them. The <c>Uow.Commit</c> span that made the rest point durable is attached as
/// an <see cref="ActivityLink"/>. Linking the whole settling job is deliberately avoided because
/// job bookkeeping can continue after the instance is already observable as Available.
/// </para>
/// <para>
/// <b>Kind is <see cref="ActivityKind.Internal"/>, deliberately.</b> apm-server classifies
/// Consumer/Server spans as transactions; a synthetic transaction would inflate throughput and skew
/// the latency distribution alerting reads. Internal renders as an ordinary bar in the waterfall.
/// Fleet-level percentiles come from <see cref="WorkflowMetrics.ActivationDurationMs"/> instead.
/// </para>
/// <para>
/// <b>This is the one explicit-parent span outside the lane helpers</b>, and it needs the same care:
/// an explicit parent leaves <see cref="Activity.Parent"/> null, so <c>Stop()</c> would set
/// <see cref="Activity.Current"/> to null and strip the caller's ambient span for the rest of its
/// frame. The explicit <see cref="Emit(ActivitySource, string, Guid, string, string, string?, string?, bool, ActivityContext)"/>
/// saves and restores it.
/// </para>
/// </summary>
public static class ActivationActivity
{
    /// <summary>Span name prefix; the suffix is the transition that settled the episode.</summary>
    public const string SpanNamePrefix = "Instance.Activation";

    /// <summary>
    /// Emits the episode span for the instance the pipeline context describes. Call it only after the
    /// settlement's writes have committed — see <see cref="ActivationVerdict"/>.
    /// </summary>
    public static Activity? Emit(
        TransitionExecutionContext context,
        ActivationVerdict verdict,
        ActivityContext settlingCommit = default)
        => Emit(
            PipelineStepActivityHelper.ActivitySource,
            verdict.Outcome,
            context.InstanceId,
            context.Domain,
            context.WorkflowKey,
            context.TransitionKey,
            verdict.StateTo ?? context.Target?.Key,
            verdict.CasFlipped,
            settlingCommit);

    /// <summary>
    /// Emits the episode span. Returns the (already stopped) activity so callers and tests can
    /// inspect it; null when nothing is listening to <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The source to start on; normally <see cref="PipelineStepActivityHelper.ActivitySource"/>.</param>
    /// <param name="outcome">One of <see cref="TelemetryConstants.ActivationOutcomes"/>.</param>
    /// <param name="instanceId">The instance that reached its rest point.</param>
    /// <param name="domain">Its domain.</param>
    /// <param name="flow">Its workflow key.</param>
    /// <param name="lastTransitionKey">The transition the settling hop ran.</param>
    /// <param name="stateTo">The state the instance rests in.</param>
    /// <param name="casFlipped">True when the settlement's compare-and-set made the instance Active.</param>
    /// <param name="settlingCommit">The completed Uow.Commit span that made the rest point durable.</param>
    public static Activity? Emit(
        ActivitySource source,
        string outcome,
        Guid instanceId,
        string domain,
        string flow,
        string? lastTransitionKey,
        string? stateTo,
        bool casFlipped = false,
        ActivityContext settlingCommit = default)
    {
        var episode = WorkflowTraceLane.Episode;
        var ambient = Activity.Current;
        var now = DateTimeOffset.UtcNow;

        // No carried episode (payload from an older build, or an entry point that seeded none):
        // cover this hop alone and say so, rather than inventing a start.
        var partial = episode is null || episode.Partial;
        var startedAt = episode?.StartedAt
                        ?? (ambient is null ? now : new DateTimeOffset(ambient.StartTimeUtc, TimeSpan.Zero));

        // The start was stamped on another replica. A clock ahead of ours would produce a negative
        // duration; clamp to zero and flag it — alert on the flag, not on the number.
        var clockSkew = startedAt > now;
        if (clockSkew) startedAt = now;

        var parent = ResolveParent(ambient);
        var links = settlingCommit != default && settlingCommit.TraceId == parent.TraceId
            ? new[] { new ActivityLink(settlingCommit) }
            : null;
        var episodeKey = episode?.TransitionKey ?? lastTransitionKey ?? "resume";
        var settlingKey = lastTransitionKey ?? episodeKey;

        var activity = source.StartActivity(
            $"{SpanNamePrefix}/{settlingKey}",
            ActivityKind.Internal,
            parent,
            tags: null,
            links,
            startTime: startedAt);

        if (activity is null)
        {
            // Nothing is listening; StartActivity did not touch Activity.Current.
            return null;
        }

        try
        {
            var durationMs = (now - startedAt).TotalMilliseconds;

            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
            activity.SetTag(TelemetryConstants.TagNames.Layer, TelemetryConstants.Layers.Orchestration);
            activity.SetTag(TelemetryConstants.TagNames.ActivationOutcome, outcome);
            activity.SetTag(TelemetryConstants.TagNames.ActivationTrigger, episode?.Trigger ?? TelemetryConstants.ActivationTriggers.Job);
            activity.SetTag(TelemetryConstants.TagNames.ActivationTransitionKey, episodeKey);
            activity.SetTag(TelemetryConstants.TagNames.ActivationHops, WorkflowTraceLane.Seq);
            activity.SetTag(TelemetryConstants.TagNames.ActivationDurationMs, durationMs);
            activity.SetTag(TelemetryConstants.TagNames.SettleCas, casFlipped ? "flipped" : "n/a");
            if (partial) activity.SetTag(TelemetryConstants.TagNames.ActivationPartial, true);
            if (clockSkew) activity.SetTag(TelemetryConstants.TagNames.ActivationClockSkew, true);
            activity.SetTag(TelemetryConstants.TagNames.InstanceId, instanceId.ToString());
            activity.SetTag(TelemetryConstants.TagNames.WorkflowInstanceId, instanceId.ToString("D").ToLowerInvariant());
            activity.SetTag(TelemetryConstants.TagNames.Domain, domain);
            activity.SetTag(TelemetryConstants.TagNames.Flow, flow);
            activity.SetTag(TelemetryConstants.TagNames.TransitionKey, lastTransitionKey);
            activity.SetTag(TelemetryConstants.TagNames.StateTo, stateTo);

            if (!partial && !clockSkew)
            {
                WorkflowMetrics.ActivationDurationMs.Record(
                    durationMs,
                    new KeyValuePair<string, object?>(TelemetryConstants.TagNames.Domain, domain),
                    new KeyValuePair<string, object?>(TelemetryConstants.TagNames.Flow, flow),
                    new KeyValuePair<string, object?>(TelemetryConstants.TagNames.ActivationTransitionKey, episodeKey),
                    new KeyValuePair<string, object?>(TelemetryConstants.TagNames.ActivationOutcome, outcome),
                    new KeyValuePair<string, object?>(TelemetryConstants.TagNames.ActivationTrigger, episode?.Trigger ?? TelemetryConstants.ActivationTriggers.Job));
            }

            activity.SetEndTime(now.UtcDateTime);
        }
        finally
        {
            activity.Stop();
            // Explicit parent ⇒ Activity.Parent == null ⇒ Stop() replaced Activity.Current with null.
            // Hand the caller back the span it was running under.
            Activity.Current = ambient;
        }

        return activity;
    }

    /// <summary>
    /// The episode's originating trace root when it belongs to the ambient trace, falling back to
    /// the lane anchor for legacy carriers and then to the ambient span. Keeping this root separate
    /// prevents a backdated child activation from starting before a later handoff parent.
    /// </summary>
    private static ActivityContext ResolveParent(Activity? ambient)
    {
        var fallback = ambient?.Context ?? default;

        var traceRoot = WorkflowTraceLane.Episode?.TraceRoot ?? WorkflowTraceLane.Current;
        if (!ActivityContext.TryParse(traceRoot, ambient?.TraceStateString, isRemote: true, out var anchor))
            return fallback;

        return ambient is null || anchor.TraceId == ambient.TraceId ? anchor : fallback;
    }
}
