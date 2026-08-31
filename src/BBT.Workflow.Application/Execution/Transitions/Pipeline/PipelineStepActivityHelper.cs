using System.Diagnostics;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Starts business-level spans for transition pipeline steps and other pipeline-scoped
/// operations (context load, validation, instance load, data append).
/// <para>
/// Step spans are ALWAYS created (Business and Verbose alike). Names deliberately avoid the
/// legacy <c>[</c> prefix: Aether's BusinessSpanFilterProcessor suppresses <c>[</c>-prefixed
/// DisplayNames at export in Business mode, which both hid the spans and re-rooted their
/// children. Prefix-free names are exported everywhere, so the step's children (task spans,
/// subflow starts, HttpClient calls) attach to a parent that really exists in the trace.
/// </para>
/// </summary>
public static class PipelineStepActivityHelper
{
    /// <summary>ActivitySource for pipeline spans. Registered in Telemetry:Tracing:AdditionalSources.</summary>
    public static readonly ActivitySource ActivitySource = new("BBT.Workflow.Pipeline");

    /// <summary>Starts the span for a pipeline step, named <c>Step.{Name}</c> (trailing "Step" trimmed).</summary>
    public static Activity? StartStepActivity(ITransitionStep step)
    {
        var activity = ActivitySource.StartActivity(
            $"Step.{TrimStepSuffix(step.Name)}",
            ActivityKind.Internal,
            Activity.Current?.Context ?? default);
        if (activity != null)
        {
            activity.SetTag(TelemetryConstants.TagNames.StepOrder, step.Order);
            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        }

        return activity;
    }

    /// <summary>
    /// Records the step's flow-control outcome (continue | stop | skipTo:{order}).
    /// <para>
    /// A step that reported <see cref="StepOutcome.NoWork"/> loses its span instead: its
    /// applicability guard did not match, so nothing happened — no lock, no task, no data write —
    /// and a zero-duration span per non-applicable step is what made the tree hard to read. The
    /// span is dropped by clearing <c>Recorded</c>, the same mechanism Aether's
    /// BusinessSpanFilterProcessor uses: exporters skip it, while it stays valid in-process so a
    /// child started inside the step (there is none, by definition) would still parent correctly.
    /// </para>
    /// </summary>
    public static void SetStepOutcome(Activity? activity, StepOutcome outcome)
    {
        if (activity is null) return;

        if (outcome.NoWork)
        {
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            return;
        }

        var value = outcome.StopPipeline ? "stop"
            : outcome.SkipToOrder is { } order ? $"skipTo:{order}"
            : "continue";
        activity.SetTag(TelemetryConstants.TagNames.StepOutcome, value);
    }

    /// <summary>Records a step failure (result error or unhandled exception) as the span's error status.</summary>
    public static void SetStepError(Activity? activity, string message)
    {
        activity?.SetStatus(ActivityStatusCode.Error, message);
    }

    /// <summary>
    /// Starts a business-level span for a pipeline-scoped operation that is not a step
    /// (e.g. Transition.LoadContext, Transition.Validate, Instance.Load, Instance.AppendData).
    /// </summary>
    public static Activity? StartOperationActivity(string operationName)
    {
        var activity = ActivitySource.StartActivity(
            operationName,
            ActivityKind.Internal,
            Activity.Current?.Context ?? default);
        activity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        return activity;
    }

    private static string TrimStepSuffix(string name)
        => name.EndsWith("Step", StringComparison.Ordinal) ? name[..^4] : name;
}
