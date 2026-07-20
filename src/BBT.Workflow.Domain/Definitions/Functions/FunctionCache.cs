using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Optional read-through cache configuration for a <see cref="Function"/>. When present, the function's
/// response is cached: on a cache hit the tasks are not executed and the cached
/// <c>FunctionResponseOutput</c> is returned; on a miss the function runs and its response is written
/// to the cache. Only side-effect-free (read) functions should enable this.
/// </summary>
public sealed class FunctionCache
{
    [JsonConstructor]
    public FunctionCache(
        ScriptCode? keyExpression = null,
        string? key = null,
        string? storeName = null,
        int? ttlInSeconds = null,
        string? consistency = null,
        bool bypassOnCacheError = true)
    {
        KeyExpression = keyExpression;
        Key = key;
        StoreName = storeName;
        TtlInSeconds = ttlInSeconds;
        Consistency = consistency;
        BypassOnCacheError = bypassOnCacheError;
    }

    /// <summary>
    /// Optional Dynamic Expresso expression (a <see cref="ScriptCode"/> with
    /// <c>location = "dynamicExpresso"</c>) that computes the cache key from the request/script context
    /// and returns a string, e.g. <c>"dcs:" + context.Headers.configKey + ":" + sha256(...)</c>.
    /// Takes precedence over <see cref="Key"/>.
    /// </summary>
    public ScriptCode? KeyExpression { get; }

    /// <summary>
    /// Optional static cache key, used when <see cref="KeyExpression"/> is absent.
    /// </summary>
    public string? Key { get; }

    /// <summary>
    /// Optional Dapr state store component name. When empty, the executing runtime's
    /// <c>DAPR_STATE_STORE_NAME</c> configuration value is used.
    /// </summary>
    public string? StoreName { get; }

    /// <summary>
    /// Optional time-to-live in seconds for the cached response. Null/non-positive means no expiry.
    /// </summary>
    public int? TtlInSeconds { get; }

    /// <summary>
    /// Optional consistency mode: <c>Eventual</c> (default) or <c>Strong</c>.
    /// </summary>
    public string? Consistency { get; }

    /// <summary>
    /// When <c>true</c> (default), cache read/write failures fall back to executing the function instead
    /// of failing the request; when <c>false</c>, a cache error surfaces as a failure.
    /// </summary>
    public bool BypassOnCacheError { get; }

    /// <summary>
    /// True when a key source (expression or static key) is configured.
    /// </summary>
    [JsonIgnore]
    public bool HasKeySource => (KeyExpression?.HasMappingCode ?? false) || !string.IsNullOrWhiteSpace(Key);
}
