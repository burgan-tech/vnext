using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// State Store Task Definition.
/// Accesses a Dapr state store component (resolved from the executing runtime's
/// <c>DAPR_STATE_STORE_NAME</c> configuration when not specified) to cache data within
/// the workflow pipeline. Supported commands: <c>get</c>, <c>set</c>, <c>delete</c>.
/// </summary>
public sealed class StateStoreTask : WorkflowTask
{
    private StateStoreTask()
    {
    }

    [JsonConstructor]
    private StateStoreTask(
        JsonElement config) : base(config)
    {
        Type = ((int)TaskType.StateStore).ToString();
    }

    /// <summary>
    /// Command to execute: <c>get</c>, <c>set</c> or <c>delete</c>.
    /// </summary>
    public string Command { get; private set; } = string.Empty;

    /// <summary>
    /// Optional Dapr state store component name. When empty, the executing runtime's
    /// <c>DAPR_STATE_STORE_NAME</c> configuration value is used.
    /// </summary>
    public string StoreName { get; private set; } = string.Empty;

    /// <summary>
    /// Cache key targeted by <c>get</c>, <c>set</c> and single-key <c>delete</c>.
    /// </summary>
    public string CacheKey { get; private set; } = string.Empty;

    /// <summary>
    /// Optional list of keys for bulk <c>delete</c>.
    /// </summary>
    public IReadOnlyList<string>? CacheKeys { get; private set; }

    /// <summary>
    /// Optional Dapr state Query API filter (JSON) for tag/pattern based <c>delete</c>.
    /// </summary>
    public JsonElement Query { get; private set; }

    /// <summary>
    /// Value written by <c>set</c>.
    /// </summary>
    public JsonElement Value { get; private set; }

    /// <summary>
    /// Optional time-to-live in seconds applied on <c>set</c> (Dapr <c>ttlInSeconds</c> metadata).
    /// </summary>
    public int? TtlInSeconds { get; private set; }

    /// <summary>
    /// Optional ETag for optimistic concurrency on read/write.
    /// </summary>
    public string? ETag { get; private set; }

    /// <summary>
    /// Optional concurrency mode: <c>FirstWrite</c> or <c>LastWrite</c>.
    /// </summary>
    public string? Concurrency { get; private set; }

    /// <summary>
    /// Optional consistency mode: <c>Eventual</c> or <c>Strong</c>.
    /// </summary>
    public string? Consistency { get; private set; }

    /// <summary>
    /// Optional additional metadata passed to the Dapr state store operation.
    /// </summary>
    public JsonElement Metadata { get; private set; }

    public void SetCommand(string command) => Command = command;
    public void SetStoreName(string storeName) => StoreName = storeName;
    public void SetCacheKey(string cacheKey) => CacheKey = cacheKey;
    public void SetCacheKeys(IReadOnlyList<string>? cacheKeys) => CacheKeys = cacheKeys;
    public void SetValue(dynamic value) => Value = JsonSerializer.SerializeToElement(value);
    public void SetTtlInSeconds(int? ttlInSeconds) => TtlInSeconds = ttlInSeconds;
    public void SetMetadata(Dictionary<string, string?> metadata)
    {
        Metadata = JsonSerializer.SerializeToElement(metadata);
    }

    /// <summary>
    /// Internal property setters for object pooling
    /// </summary>
    internal void SetCommandInternal(string command) => Command = command;
    internal void SetStoreNameInternal(string storeName) => StoreName = storeName;
    internal void SetCacheKeyInternal(string cacheKey) => CacheKey = cacheKey;
    internal void SetCacheKeysInternal(IReadOnlyList<string>? cacheKeys) => CacheKeys = cacheKeys;
    internal void SetQueryInternal(JsonElement query) => Query = query;
    internal void SetValueInternal(JsonElement value) => Value = value;
    internal void SetTtlInSecondsInternal(int? ttlInSeconds) => TtlInSeconds = ttlInSeconds;
    internal void SetETagInternal(string? etag) => ETag = etag;
    internal void SetConcurrencyInternal(string? concurrency) => Concurrency = concurrency;
    internal void SetConsistencyInternal(string? consistency) => Consistency = consistency;
    internal void SetMetadataInternal(JsonElement metadata) => Metadata = metadata;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("command", out var command))
            Command = command.GetString() ?? throw new ArgumentNullException(nameof(command));

        if (config.TryGetProperty("storeName", out var storeName))
        {
            var vStoreName = storeName.GetString();
            StoreName = string.IsNullOrWhiteSpace(vStoreName) ? string.Empty : vStoreName;
        }

        if (config.TryGetProperty("key", out var key))
            CacheKey = key.GetString() ?? string.Empty;

        if (config.TryGetProperty("keys", out var keysElement) &&
            keysElement.ValueKind == JsonValueKind.Array)
        {
            var keys = keysElement.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!)
                .ToList();
            CacheKeys = keys.Count > 0 ? keys : null;
        }

        if (config.TryGetProperty("query", out var query))
            Query = query;

        if (config.TryGetProperty("value", out var value))
            Value = value;

        if (config.TryGetProperty("ttlInSeconds", out var ttl) &&
            ttl.ValueKind == JsonValueKind.Number)
            TtlInSeconds = ttl.GetInt32();

        if (config.TryGetProperty("etag", out var etag))
            ETag = etag.GetString();

        if (config.TryGetProperty("concurrency", out var concurrency))
            Concurrency = concurrency.GetString();

        if (config.TryGetProperty("consistency", out var consistency))
            Consistency = consistency.GetString();

        if (config.TryGetProperty("metadata", out var metadata))
            Metadata = metadata;
    }

    public override WorkflowTask Clone()
    {
        return CloneTyped();
    }

    /// <summary>
    /// Creates a typed deep copy of the current StateStoreTask instance.
    /// </summary>
    public StateStoreTask CloneTyped()
    {
        var cloned = new StateStoreTask();
        CopyBaseTo(cloned);

        cloned.Command = Command;
        cloned.StoreName = StoreName;
        cloned.CacheKey = CacheKey;
        cloned.CacheKeys = CacheKeys;
        cloned.Query = Query;
        cloned.Value = Value;
        cloned.TtlInSeconds = TtlInSeconds;
        cloned.ETag = ETag;
        cloned.Concurrency = Concurrency;
        cloned.Consistency = Consistency;
        cloned.Metadata = Metadata;

        return cloned;
    }

    /// <summary>
    /// Internal method for object pooling - copies all properties efficiently
    /// </summary>
    /// <param name="source">Source task to copy from</param>
    public void CopyFromInternal(StateStoreTask source)
    {
        source.CopyBaseToInternal(this);
        SetCommandInternal(source.Command);
        SetStoreNameInternal(source.StoreName);
        SetCacheKeyInternal(source.CacheKey);
        SetCacheKeysInternal(source.CacheKeys);
        SetQueryInternal(source.Query);
        SetValueInternal(source.Value);
        SetTtlInSecondsInternal(source.TtlInSeconds);
        SetETagInternal(source.ETag);
        SetConcurrencyInternal(source.Concurrency);
        SetConsistencyInternal(source.Consistency);
        SetMetadataInternal(source.Metadata);
    }

    /// <summary>
    /// Resets the task instance to a clean state for object pooling
    /// </summary>
    public override void Reset()
    {
        base.Reset();
        Command = string.Empty;
        StoreName = string.Empty;
        CacheKey = string.Empty;
        CacheKeys = null;
        Query = default;
        Value = default;
        TtlInSeconds = null;
        ETag = null;
        Concurrency = null;
        Consistency = null;
        Metadata = default;
    }

    /// <summary>
    /// Creates a new instance for object pooling - internal use only
    /// </summary>
    public static StateStoreTask CreateEmpty()
    {
        return new StateStoreTask();
    }

    public static StateStoreTask Create(
        JsonElement config)
    {
        return new StateStoreTask(config);
    }
}
