using System.Diagnostics;
using BBT.Aether.Telemetry;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Starts diagnostic spans for transition pipeline steps — but ONLY in Verbose detail level.
/// <para>
/// In Business mode a step span must not exist at all: Aether's Business filter suppresses
/// <c>[</c>-prefixed spans at <c>OnEnd</c> (export time), which means a created-but-filtered step
/// Activity still becomes <c>Activity.Current</c> for the whole step body — every child started
/// inside the step (task coordinator spans, subflow starts, background-job enqueues, outbound
/// HttpClient calls) then points at a parent span id that is never exported, and trace UIs
/// re-root the entire subtree. Gating CREATION on <see cref="AetherTracingRuntime.IsVerbose"/>
/// keeps <c>Activity.Current</c> on the transition span in Business mode, so all children attach
/// to the pipeline where they belong.
/// </para>
/// </summary>
public static class PipelineStepActivityHelper
{
    /// <summary>
    /// ActivitySource for pipeline-step spans. Registered in Telemetry:Tracing:AdditionalSources;
    /// only emits when DetailLevel is Verbose.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new("BBT.Workflow.Pipeline");

    /// <summary>
    /// Starts the diagnostic span for a pipeline step, named with the established
    /// <c>[{Order}] {StepName}</c> convention. Returns null in Business mode so the step leaves
    /// no hole in the parent chain.
    /// </summary>
    public static Activity? StartStepActivity(ITransitionStep step)
    {
        if (!AetherTracingRuntime.IsVerbose)
        {
            return null;
        }

        var activity = ActivitySource.StartActivity(
            $"[{step.Order}] {step.Name}",
            ActivityKind.Internal,
            Activity.Current?.Context ?? default);
        activity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Diagnostic);
        return activity;
    }
}
