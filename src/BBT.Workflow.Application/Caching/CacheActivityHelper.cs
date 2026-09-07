using System.Diagnostics;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Caching;

/// <summary>
/// Provides centralized tracing for distributed cache operations (get, set, invalidate, warmup).
/// Creates child spans under the current activity so Dapr state store timing is correctly
/// positioned in traces instead of appearing as detached spans at the bottom.
/// <para>
/// Cache spans are ALWAYS created (Business and Verbose alike; Task 10, following the
/// <c>PipelineStepActivityHelper</c> precedent from Task 3). L1/L2 hit visibility is a business
/// signal, not a verbose-only diagnostic — component cache reads are on the hot path of every
/// transition's context load. Names deliberately avoid the legacy <c>[</c> prefix so Aether's
/// BusinessSpanFilterProcessor never suppresses them at export in Business mode.
/// </para>
/// </summary>
public static class CacheActivityHelper
{
    /// <summary>
    /// ActivitySource for distributed cache operations.
    /// When using explicit OpenTelemetry source registration, add this source to the TracerProvider
    /// (e.g. <c>AddSource("BBT.Workflow.Cache")</c>). If the host uses a wildcard such as
    /// <c>AddSource("BBT.Workflow.*")</c>, no extra registration is needed.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(TelemetryConstants.ActivitySources.Cache);

    public const string OperationGet = "Cache.Get";
    public const string OperationSet = "Cache.Set";

    /// <summary>
    /// The write-back that follows a read miss, as opposed to <see cref="OperationSet"/>, which is an
    /// explicit publish. They are deliberately separate names: one is traffic the cache creates for
    /// itself on the read path and belongs under the <c>Cache.Get</c> that caused it, the other is a
    /// caller writing on purpose. Merging them would make the read path's cost unattributable.
    /// </summary>
    public const string OperationWrite = "Cache.Write";

    public const string OperationRemove = "Cache.Remove";
    public const string OperationWarmup = "Cache.Warmup";
    public const string OperationGenerationGet = "Cache.GenerationGet";
    public const string OperationGenerationSet = "Cache.GenerationSet";

    /// <summary>The in-process (L1) envelope cache answered the read.</summary>
    public const string SourceL1 = "l1";

    /// <summary>The distributed store answered the read; L1 either missed or is disabled.</summary>
    public const string SourceL2 = "l2";

    /// <summary>Neither cache layer answered — the value came from the backing store.</summary>
    public const string SourceBackend = "backend";

    private const string TagCacheKey = "cache.key";
    private const string TagCacheHit = "cache.hit";
    private const string TagCacheSource = "cache.source";
    private const string TagCacheL1Hit = "cache.l1.hit";
    private const string TagCacheNegative = "cache.negative";
    private const string TagCacheCoalesced = "cache.coalesced";
    private const string TagCacheGeneration = "cache.generation";
    private const string TagCacheStore = "cache.store";
    private const string TagCacheItemCount = "cache.item_count";
    private const string TagComponentType = "cache.component_type";

    /// <summary>
    /// Starts a new activity as a child of the current activity for a cache operation.
    /// Returns null if no listener is registered — zero allocation in that case.
    /// <para>
    /// When a cache key is supplied the span is named <c>{operation}/{cacheKey}</c>, so a reader
    /// sees WHICH component a read or write touched straight from the tree instead of having to
    /// open the span for its <c>cache.key</c> tag. The tag is still written, because that is what
    /// queries and aggregations group on. A keyless operation (warmup, batch) keeps the bare
    /// operation name rather than growing a trailing slash.
    /// </para>
    /// </summary>
    public static Activity? StartActivity(
        string operationName,
        string? cacheKey = null,
        string? componentType = null)
    {
        var spanName = string.IsNullOrEmpty(cacheKey) ? operationName : $"{operationName}/{cacheKey}";

        var activity = ActivitySource.StartActivity(
            spanName,
            ActivityKind.Client);

        if (activity is not null)
        {
            activity.SetTag(TagCacheStore, "dapr");
            activity.SetTag(TelemetryConstants.TagNames.Layer, TelemetryConstants.Layers.Orchestration);
            activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
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
    /// Records which layer actually answered the read — <see cref="SourceL1"/>, <see cref="SourceL2"/>
    /// or <see cref="SourceBackend"/>.
    /// </summary>
    /// <remarks>
    /// Complements rather than replaces <c>cache.hit</c> / <c>cache.l1.hit</c>: those two still say
    /// whether the cache answered at all and which tier, but deriving "served from the backend" from
    /// them requires reading a combination. One tag states it, so a query can group by it directly.
    /// </remarks>
    public static void SetSource(Activity? activity, string source)
    {
        activity?.SetTag(TagCacheSource, source);
    }

    /// <summary>
    /// Records whether the in-process (L1) envelope cache answered the read, so load tests can
    /// attribute latency between L1 and the distributed store.
    /// </summary>
    public static void SetL1Hit(Activity? activity, bool hit)
    {
        activity?.SetTag(TagCacheL1Hit, hit);
    }

    /// <summary>
    /// Records the number of items in a batch/warmup operation.
    /// </summary>
    public static void SetItemCount(Activity? activity, int count)
    {
        activity?.SetTag(TagCacheItemCount, count);
    }

    /// <summary>
    /// Records that the hit was a negative (no matching version) entry rather than a component body.
    /// </summary>
    public static void SetNegative(Activity? activity, bool negative)
    {
        activity?.SetTag(TagCacheNegative, negative);
    }

    /// <summary>
    /// Records that this call waited on a resolution already in flight instead of loading from the
    /// backend itself.
    /// </summary>
    public static void SetCoalesced(Activity? activity, bool coalesced)
    {
        activity?.SetTag(TagCacheCoalesced, coalesced);
    }

    /// <summary>
    /// Records the generation token a version resolution was scoped to.
    /// </summary>
    public static void SetGeneration(Activity? activity, string? generation)
    {
        if (activity is not null && !string.IsNullOrEmpty(generation))
            activity.SetTag(TagCacheGeneration, generation);
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
