using System.Diagnostics;

namespace BBT.Workflow.Caching;

/// <summary>
/// Provides centralized tracing for distributed cache operations (get, set, invalidate, warmup).
/// Creates child spans under the current activity so Dapr state store timing is correctly
/// positioned in traces instead of appearing as detached spans at the bottom.
/// </summary>
public static class CacheActivityHelper
{
    /// <summary>
    /// ActivitySource for distributed cache operations.
    /// When using explicit OpenTelemetry source registration, add this source to the TracerProvider
    /// (e.g. <c>AddSource("BBT.Workflow.Cache")</c>). If the host uses a wildcard such as
    /// <c>AddSource("BBT.Workflow.*")</c>, no extra registration is needed.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new("BBT.Workflow.Cache");

    public const string OperationGet = "Cache.Get";
    public const string OperationSet = "Cache.Set";
    public const string OperationRemove = "Cache.Remove";
    public const string OperationWarmup = "Cache.Warmup";
    public const string OperationVersionIndex = "Cache.VersionIndex";

    private const string TagCacheKey = "cache.key";
    private const string TagCacheHit = "cache.hit";
    private const string TagCacheStore = "cache.store";
    private const string TagCacheItemCount = "cache.item_count";
    private const string TagComponentType = "cache.component_type";

    /// <summary>
    /// Starts a new activity as a child of the current activity for a cache operation.
    /// Returns null if no listener is registered — zero allocation in that case.
    /// </summary>
    public static Activity? StartActivity(
        string operationName,
        string? cacheKey = null,
        string? componentType = null)
    {
        var activity = ActivitySource.StartActivity(
            operationName,
            ActivityKind.Client,
            Activity.Current?.Context ?? default);

        if (activity is not null)
        {
            activity.SetTag(TagCacheStore, "dapr");
            if (!string.IsNullOrEmpty(cacheKey))
                activity.SetTag(TagCacheKey, cacheKey);
            if (!string.IsNullOrEmpty(componentType))
                activity.SetTag(TagComponentType, componentType);
        }

        return activity;
    }

    /// <summary>
    /// Records whether the distributed cache returned a hit or miss.
    /// </summary>
    public static void SetCacheHit(Activity? activity, bool hit)
    {
        activity?.SetTag(TagCacheHit, hit);
    }

    /// <summary>
    /// Records the number of items in a batch/warmup operation.
    /// </summary>
    public static void SetItemCount(Activity? activity, int count)
    {
        activity?.SetTag(TagCacheItemCount, count);
    }

    /// <summary>
    /// Sets the activity status to Error and records the exception.
    /// </summary>
    public static void SetError(Activity? activity, Exception exception)
    {
        if (activity is null) return;
        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.AddException(exception);
    }
}
