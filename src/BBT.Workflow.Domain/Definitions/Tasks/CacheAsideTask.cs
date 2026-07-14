using System.Text.Json;
using System.Text.Json.Serialization;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Cache-Aside (read-through) Task Definition.
/// Implements the cache-aside pattern as a first-class workflow task:
/// <list type="number">
///   <item>Attempts to read the value from a configured Dapr state store (the cache).</item>
///   <item>On a cache miss (or when <see cref="ForceRefresh"/> is set), executes the referenced
///   <see cref="SourceTask"/>, applies the optional <see cref="SourceMapping"/> to shape the result,
///   writes the shaped value back to the cache with <see cref="TtlInSeconds"/>, and returns it.</item>
/// </list>
/// This removes the need for workflow designers to hand-wire "check cache → call service → write cache"
/// as three separate tasks, centralizing TTL, consistency and cache-failure semantics in the engine.
/// </summary>
public sealed class CacheAsideTask : WorkflowTask
{
    private CacheAsideTask()
    {
    }

    [JsonConstructor]
    private CacheAsideTask(
        JsonElement config) : base(config)
    {
        Type = ((int)TaskType.CacheAside).ToString();
    }

    /// <summary>
    /// Cache key, used verbatim. A dynamic key is computed by a mapping <c>InputHandler</c> that calls
    /// <see cref="SetCacheKey"/> (the standard mapping mechanism), exactly as the State Store task does.
    /// </summary>
    public string CacheKey { get; private set; } = string.Empty;

    /// <summary>
    /// Optional Dapr state store component name used as the cache. When empty, the executing runtime's
    /// <c>DAPR_STATE_STORE_NAME</c> configuration value is used.
    /// </summary>
    public string StoreName { get; private set; } = string.Empty;

    /// <summary>
    /// Time-to-live in seconds for cached entries. When absent or <c>0</c>, the entry has no expiry.
    /// </summary>
    public int? TtlInSeconds { get; private set; }

    /// <summary>
    /// Optional consistency mode passed through to the Dapr state store: <c>Eventual</c> (default) or <c>Strong</c>.
    /// </summary>
    public string? Consistency { get; private set; }

    /// <summary>
    /// Reference (key/domain/flow/version) to the task executed on a cache miss.
    /// </summary>
    public Reference SourceTask { get; private set; } = null!;

    /// <summary>
    /// Optional mapping applied to the cached (raw source) result before it is returned. Runs as the
    /// mapping's <c>OutputHandler</c> in the executor's output stage, on both hits and misses.
    /// </summary>
    public ScriptCode? SourceMapping { get; private set; }

    /// <summary>
    /// Optional Dynamic Expresso expression (a <see cref="ScriptCode"/> with
    /// <c>location = "dynamicExpresso"</c>) that computes the cache key from the request/script context,
    /// e.g. <c>"customer:" + context.Headers.customerId + ":profile"</c>. When present, its evaluated
    /// string result overrides <see cref="CacheKey"/> at runtime — the lightweight way to derive a
    /// vary-by-correct key from user-supplied data without a full <c>.csx</c> mapping.
    /// </summary>
    public ScriptCode? KeyExpression { get; private set; }

    /// <summary>
    /// When <c>true</c> (default), cache read/write failures fall back to the source task instead of failing
    /// the pipeline. When <c>false</c>, cache errors surface as task failure (error boundary applies).
    /// </summary>
    public bool BypassOnCacheError { get; private set; } = true;

    /// <summary>
    /// When <c>true</c>, the cache read is skipped: the source task is always executed and the cache entry overwritten.
    /// </summary>
    public bool ForceRefresh { get; private set; }

    public void SetCacheKey(string cacheKey) => CacheKey = cacheKey;
    public void SetStoreName(string storeName) => StoreName = storeName;
    public void SetTtlInSeconds(int? ttlInSeconds) => TtlInSeconds = ttlInSeconds;

    /// <summary>
    /// Internal property setters for object pooling.
    /// </summary>
    internal void SetCacheKeyInternal(string cacheKey) => CacheKey = cacheKey;
    internal void SetStoreNameInternal(string storeName) => StoreName = storeName;
    internal void SetTtlInSecondsInternal(int? ttlInSeconds) => TtlInSeconds = ttlInSeconds;
    internal void SetConsistencyInternal(string? consistency) => Consistency = consistency;
    internal void SetSourceTaskInternal(Reference sourceTask) => SourceTask = sourceTask;
    internal void SetSourceMappingInternal(ScriptCode? sourceMapping) => SourceMapping = sourceMapping;
    internal void SetKeyExpressionInternal(ScriptCode? keyExpression) => KeyExpression = keyExpression;
    internal void SetBypassOnCacheErrorInternal(bool bypassOnCacheError) => BypassOnCacheError = bypassOnCacheError;
    internal void SetForceRefreshInternal(bool forceRefresh) => ForceRefresh = forceRefresh;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("key", out var key))
            CacheKey = key.GetString() ?? string.Empty;

        if (config.TryGetProperty("storeName", out var storeName))
        {
            var vStoreName = storeName.GetString();
            StoreName = string.IsNullOrWhiteSpace(vStoreName) ? string.Empty : vStoreName;
        }

        if (config.TryGetProperty("ttlInSeconds", out var ttl) &&
            ttl.ValueKind == JsonValueKind.Number)
            TtlInSeconds = ttl.GetInt32();

        if (config.TryGetProperty("consistency", out var consistency))
            Consistency = consistency.GetString();

        if (config.TryGetProperty("sourceTask", out var sourceTask) &&
            sourceTask.ValueKind == JsonValueKind.Object)
            SourceTask = ParseReference(sourceTask);

        if (config.TryGetProperty("sourceMapping", out var sourceMapping) &&
            sourceMapping.ValueKind == JsonValueKind.Object)
            SourceMapping = sourceMapping.Deserialize<ScriptCode>(JsonSerializerConstants.JsonOptions);

        if (config.TryGetProperty("keyExpression", out var keyExpression) &&
            keyExpression.ValueKind == JsonValueKind.Object)
            KeyExpression = keyExpression.Deserialize<ScriptCode>(JsonSerializerConstants.JsonOptions);

        if (config.TryGetProperty("bypassOnCacheError", out var bypass) &&
            (bypass.ValueKind == JsonValueKind.True || bypass.ValueKind == JsonValueKind.False))
            BypassOnCacheError = bypass.GetBoolean();

        if (config.TryGetProperty("forceRefresh", out var force) &&
            (force.ValueKind == JsonValueKind.True || force.ValueKind == JsonValueKind.False))
            ForceRefresh = force.GetBoolean();
    }

    /// <summary>
    /// Parses a task reference object (<c>key</c>/<c>domain</c>/<c>flow</c>/<c>version</c>).
    /// <c>flow</c> defaults to the runtime tasks schema when omitted.
    /// </summary>
    private static Reference ParseReference(JsonElement element)
    {
        var key = element.TryGetProperty("key", out var k) ? k.GetString() : null;
        var domain = element.TryGetProperty("domain", out var d) ? d.GetString() : null;
        var flow = element.TryGetProperty("flow", out var f) ? f.GetString() : null;
        var version = element.TryGetProperty("version", out var v) ? v.GetString() : null;

        return new Reference(
            key ?? throw new ArgumentException("sourceTask.key is required for a CacheAside task.", nameof(element)),
            domain ?? throw new ArgumentException("sourceTask.domain is required for a CacheAside task.", nameof(element)),
            string.IsNullOrWhiteSpace(flow) ? RuntimeSysSchemaInfo.Tasks : flow,
            version ?? throw new ArgumentException("sourceTask.version is required for a CacheAside task.", nameof(element)));
    }

    public override WorkflowTask Clone()
    {
        return CloneTyped();
    }

    /// <summary>
    /// Creates a typed deep copy of the current CacheAsideTask instance.
    /// </summary>
    public CacheAsideTask CloneTyped()
    {
        var cloned = new CacheAsideTask();
        CopyBaseTo(cloned);

        cloned.CacheKey = CacheKey;
        cloned.StoreName = StoreName;
        cloned.TtlInSeconds = TtlInSeconds;
        cloned.Consistency = Consistency;
        cloned.SourceTask = SourceTask;
        cloned.SourceMapping = SourceMapping;
        cloned.KeyExpression = KeyExpression;
        cloned.BypassOnCacheError = BypassOnCacheError;
        cloned.ForceRefresh = ForceRefresh;

        return cloned;
    }

    /// <summary>
    /// Internal method for object pooling - copies all properties efficiently.
    /// </summary>
    /// <param name="source">Source task to copy from.</param>
    public void CopyFromInternal(CacheAsideTask source)
    {
        source.CopyBaseToInternal(this);
        SetCacheKeyInternal(source.CacheKey);
        SetStoreNameInternal(source.StoreName);
        SetTtlInSecondsInternal(source.TtlInSeconds);
        SetConsistencyInternal(source.Consistency);
        SetSourceTaskInternal(source.SourceTask);
        SetSourceMappingInternal(source.SourceMapping);
        SetKeyExpressionInternal(source.KeyExpression);
        SetBypassOnCacheErrorInternal(source.BypassOnCacheError);
        SetForceRefreshInternal(source.ForceRefresh);
    }

    /// <summary>
    /// Resets the task instance to a clean state for object pooling.
    /// </summary>
    public override void Reset()
    {
        base.Reset();
        CacheKey = string.Empty;
        StoreName = string.Empty;
        TtlInSeconds = null;
        Consistency = null;
        SourceTask = null!;
        SourceMapping = null;
        KeyExpression = null;
        BypassOnCacheError = true;
        ForceRefresh = false;
    }

    /// <summary>
    /// Creates a new instance for object pooling - internal use only.
    /// </summary>
    public static CacheAsideTask CreateEmpty()
    {
        return new CacheAsideTask();
    }

    public static CacheAsideTask Create(
        JsonElement config)
    {
        return new CacheAsideTask(config);
    }
}
