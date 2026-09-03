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
}
