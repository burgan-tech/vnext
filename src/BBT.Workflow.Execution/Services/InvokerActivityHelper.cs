using System.Diagnostics;

namespace BBT.Workflow.Execution.Services;

/// <summary>
/// Tracing for the Execution service's invoker layer.
/// <para>
/// Until this existed the Execution service produced no spans of its own: a trace showed the
/// orchestration-side <c>Task.Invoke</c>, then the HTTP/Dapr client span, and then nothing until
/// the response came back. Everything the Execution side actually did — which invoker ran, whether
/// a cache-aside read hit, and above all the SOURCE task a cache-aside miss invokes — was invisible.
/// </para>
/// <para>
/// One span at the registry is what makes the nested case work: <c>CacheAsideTaskInvoker</c> calls
/// back into the registry for its source task, so that task gets its own span for free rather than
/// needing per-invoker instrumentation.
/// </para>
/// <para>
/// The source name is covered by the <c>BBT.Workflow.Execution*</c> wildcard already present in the
/// Execution host's <c>Telemetry:Tracing:AdditionalSources</c>.
/// </para>
/// </summary>
public static class InvokerActivityHelper
{
    /// <summary>ActivitySource for Execution-side task invocation.</summary>
    public static readonly ActivitySource ActivitySource = new("BBT.Workflow.Execution.Invokers");

    /// <summary>Tag: the invoked task's key.</summary>
    public const string TagTaskKey = "vnext.task.key";

    /// <summary>Tag: the invoked task's type.</summary>
    public const string TagTaskType = "vnext.task.type";

    /// <summary>Tag: whether a cache-aside read was served from the cache.</summary>
    public const string TagCacheHit = "cache.hit";

    /// <summary>
    /// Starts the span covering one task invocation, named <c>Invoke.{taskType}/{taskKey}</c> so the
    /// tree names the work without the reader opening the span.
    /// </summary>
    public static Activity? StartInvokeActivity(string taskType, string taskKey)
    {
        var activity = ActivitySource.StartActivity(
            $"Invoke.{taskType}/{taskKey}",
            ActivityKind.Internal,
            Activity.Current?.Context ?? default);

        if (activity is not null)
        {
            activity.SetTag(TagTaskKey, taskKey);
            activity.SetTag(TagTaskType, taskType);
        }

        return activity;
    }

    /// <summary>
    /// Starts the span covering everything an invoker does BEFORE its outbound call — binding
    /// deserialization, client construction, header/URL/body preparation. Dispose it immediately
    /// before the I/O call so the trace separates "our prep" from "their latency". Always-on:
    /// this gap measured 27 ms in the trace that motivated it, with nothing to attribute it to.
    /// </summary>
    public static Activity? StartPrepareActivity(string taskType, string taskKey)
    {
        var activity = ActivitySource.StartActivity("Invoke.Prepare", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag(TagTaskKey, taskKey);
            activity.SetTag(TagTaskType, taskType);
        }
        return activity;
    }

    /// <summary>
    /// Starts a span for one step of the cache-aside protocol (<c>CacheAside.Read</c> /
    /// <c>CacheAside.Write</c>), so a hit and a miss are told apart in the tree rather than inferred
    /// from whether a source-task span happens to follow.
    /// </summary>
    public static Activity? StartCacheAsideActivity(string operation, string cacheKey)
        => ActivitySource.StartActivity(
            $"CacheAside.{operation}/{cacheKey}",
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

    /// <summary>Records whether the cache-aside read was a hit.</summary>
    public static void SetCacheHit(Activity? activity, bool hit) => activity?.SetTag(TagCacheHit, hit);

    /// <summary>Marks the span failed with the invocation's error message.</summary>
    public static void SetError(Activity? activity, string? message)
        => activity?.SetStatus(ActivityStatusCode.Error, message);
}
