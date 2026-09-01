using System.Diagnostics;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Spans for the script engine: compilation (cold cost incl. helper-set builds) and execution.
/// <para>
/// NOTE: this reverses the earlier "no compile span" decision (2026-08 script-perf work) — a
/// user decision on 2026-08-25 (see docs/superpowers/specs/2026-08-25-trace-span-tree-design.md).
/// The <see cref="ScriptCompileTelemetry"/> accumulator tags and <c>script.compile</c> event are
/// kept alongside for query compatibility.
/// </para>
/// </summary>
public static class ScriptActivityHelper
{
    /// <summary>ActivitySource for script spans. Registered in Telemetry:Tracing:AdditionalSources.</summary>
    public static readonly ActivitySource ActivitySource = new("BBT.Workflow.Scripting");

    /// <summary>
    /// Starts the span covering one compile call, named <c>Script.Compile/{identity}</c> so the tree
    /// says WHICH script compiled without the reader opening the span.
    /// </summary>
    /// <param name="identity">
    /// <see cref="BBT.Workflow.Definitions.ScriptCode.TraceIdentity"/> when the caller has a
    /// <c>ScriptCode</c>. The raw-string compile overloads have none, and fall back to the bare
    /// <c>Script.Compile</c> name. Obtaining <c>identity</c> itself is free (already a materialized
    /// string); the <c>$"Script.Compile/{identity}"</c> interpolation below is the one small
    /// allocation on this path, and it happens unconditionally — before
    /// <see cref="ActivitySource.StartActivity(string,ActivityKind,ActivityContext)"/> is called, so
    /// even with no listener attached. Negligible in absolute terms (the broader compile path
    /// already does comparable work, e.g. <c>ScriptCompileTelemetry.FindTargetActivity</c>'s span
    /// walk), just not literally allocation-free.
    /// </param>
    public static Activity? StartCompileActivity(string? identity = null)
    {
        var activity = ActivitySource.StartActivity(
            string.IsNullOrEmpty(identity) ? "Script.Compile" : $"Script.Compile/{identity}",
            ActivityKind.Internal,
            Activity.Current?.Context ?? default);

        activity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        return activity;
    }

    /// <summary>Stamps the compile outcome; any non-success status marks the span as error.</summary>
    public static void SetCompileResult(Activity? activity, bool cacheMiss, string status)
    {
        if (activity is null) return;
        activity.SetTag(TelemetryConstants.TagNames.ScriptCacheHit, !cacheMiss);
        if (!string.Equals(status, "success", StringComparison.Ordinal))
            activity.SetStatus(ActivityStatusCode.Error, status);
    }

    /// <summary>
    /// Starts the span covering one script invocation at a call site that no existing parent span
    /// delimits (lock-key scripts, subflow input/output mappings, function output handlers). Task
    /// input/output mappings are deliberately NOT wrapped — Task.PrepareInput / Task.ProcessOutput
    /// already delimit them.
    /// delimits (lock-key scripts, subflow mappings). Task input/output mappings are deliberately
    /// NOT wrapped — Task.PrepareInput / Task.ProcessOutput already delimit them.
    /// </summary>
    public static Activity? StartExecuteActivity(string scriptKind)
    {
        var activity = ActivitySource.StartActivity("Script.Execute", ActivityKind.Internal, Activity.Current?.Context ?? default);
        if (activity != null)
        {
            activity.SetTag(TelemetryConstants.TagNames.ScriptKind, scriptKind);
            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        }

        return activity;
    }

    /// <summary>Starts the span covering a helper-set resolve + compile (the invisible ~2s cold cost).</summary>
    public static Activity? StartResolveHelpersActivity(int helperCount)
    {
        var activity = ActivitySource.StartActivity("Script.ResolveHelpers", ActivityKind.Internal, Activity.Current?.Context ?? default);
        if (activity != null)
        {
            activity.SetTag(TelemetryConstants.TagNames.ScriptHelperCount, helperCount);
            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        }

        return activity;
    }
}
