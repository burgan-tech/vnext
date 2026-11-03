using System.Diagnostics.Metrics;

namespace BBT.Workflow.Telemetry;

/// <summary>
/// Business metrics published on the <c>BBT.Workflow.Telemetry</c> meter (register it in
/// <c>Telemetry:Metrics:AdditionalMeters</c>; the same meter name is used by
/// <c>DuplicateTolerantTraceContextPropagator</c> in the HttpApi.Shared layer — several
/// <see cref="Meter"/> instances may share a name, and one <c>AddMeter</c> subscribes to all of them).
/// </summary>
public static class WorkflowMetrics
{
    /// <summary>Meter name; must match the value registered in every host's <c>AdditionalMeters</c>.</summary>
    public const string MeterName = "BBT.Workflow.Telemetry";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// Trigger → rest-point duration of an activation episode, in milliseconds — what a client waited
    /// between sending a transition (or start) request and the instance becoming observable at rest.
    /// Tagged by <c>vnext.domain</c>, <c>vnext.flow.key</c>, <c>vnext.activation.transition.key</c>,
    /// <c>vnext.activation.outcome</c>, <c>vnext.activation.trigger</c>. Partial episodes (start not
    /// carried) are NOT recorded, so the histogram only ever holds genuine end-to-end values.
    /// </summary>
    public static readonly Histogram<double> ActivationDurationMs = Meter.CreateHistogram<double>(
        "workflow_activation_duration_ms",
        unit: "ms",
        description: "Trigger-to-rest duration of a workflow instance activation episode.");

    /// <summary>
    /// One count per resume request of a pipeline paused for declarative long-poll termination.
    /// Tagged by <c>vnext.domain</c>, <c>vnext.flow.key</c>, <c>vnext.activation.trigger</c>
    /// (<c>ack</c> = the client acknowledged, <c>ack-timeout</c> = the fallback job fired because it
    /// never did) and <c>vnext.longpoll.ack_resume.outcome</c> (<c>resumed</c>, <c>not_awaiting</c>,
    /// <c>instance_not_found</c>, <c>failed</c>). A flow whose <c>ack-timeout</c> count tracks its
    /// <c>resumed</c> count has a client that never acknowledges: every approval waits the whole
    /// <c>fallbackTimeoutSeconds</c> window (morph-idm, test, 2026-11-03: 36 of 36).
    /// </summary>
    public static readonly Counter<long> LongPollResumes = Meter.CreateCounter<long>(
        "workflow_longpoll_resume_total",
        description: "Resume requests of a pipeline paused for long-poll termination, by trigger and outcome.");
}
