using System.Diagnostics;
using BBT.Aether.Telemetry;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Starts spans for transition pipeline steps. Two kinds live here, with different gating:
/// diagnostic step spans (<c>[{Order}] {StepName}</c>, Verbose-only) and business lifecycle
/// spans (<c>OnExit.{state}</c> / <c>OnEntry.{state}</c>, always created).
/// <para>
/// Step spans exist ONLY in Verbose detail level. In Business mode a step span must not exist at
/// all: Aether's Business filter suppresses <c>[</c>-prefixed spans at <c>OnEnd</c> (export
/// time), which means a created-but-filtered step Activity still becomes <c>Activity.Current</c>
/// for the whole step body — every child started inside the step (task coordinator spans,
/// subflow starts, background-job enqueues, outbound HttpClient calls) then points at a parent
/// span id that is never exported, and trace UIs re-root the entire subtree. Gating CREATION on
/// <see cref="AetherTracingRuntime.IsVerbose"/> keeps <c>Activity.Current</c> on the transition
/// span in Business mode, so all children attach to the pipeline where they belong.
/// </para>
/// <para>
/// Lifecycle spans are the opposite case: their names carry no <c>[</c> prefix, so the Business
/// filter keeps them, and they group a state's OnExit/OnEntry task spans under one node — the
/// state-transition shape of the trace. They are therefore created in BOTH modes.
/// </para>
/// </summary>
public static class PipelineStepActivityHelper
{
    /// <summary>
    /// ActivitySource for pipeline-step and lifecycle spans. Registered in
    /// Telemetry:Tracing:AdditionalSources; step spans only emit when DetailLevel is Verbose,
    /// lifecycle spans always.
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

    /// <summary>
    /// Starts a business-level state-lifecycle span (<c>OnExit.{stateKey}</c> /
    /// <c>OnEntry.{stateKey}</c>) grouping that state's task spans under one node.
    /// <para>
    /// NOT gated on verbose tracing: without it the OnExit/OnEntry task spans sit directly under
    /// <c>transition/{key}</c>, indistinguishable from the transition's own OnExecute tasks. The
    /// name carries no <c>[</c> prefix, so the Business export filter keeps it — the creation
    /// rule above holds.
    /// </para>
    /// </summary>
    /// <param name="operationName">Lifecycle phase name, e.g. <c>OnExit</c> or <c>OnEntry</c>.</param>
    /// <param name="stateKey">The state whose lifecycle tasks run (becomes part of the name).</param>
    /// <param name="taskCount">How many tasks the phase is about to run.</param>
    public static Activity? StartLifecycleActivity(string operationName, string stateKey, int taskCount)
    {
        var activity = ActivitySource.StartActivity(
            $"{operationName}.{stateKey}",
            ActivityKind.Internal,
            Activity.Current?.Context ?? default);

        if (activity != null)
        {
            activity.SetTag(TelemetryConstants.TagNames.StateKey, stateKey);
            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
            activity.SetTag("vnext.task.count", taskCount);
        }

        return activity;
    }
}
