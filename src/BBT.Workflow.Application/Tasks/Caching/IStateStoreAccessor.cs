using System.Text.Json;

namespace BBT.Workflow.Tasks.Caching;

/// <summary>
/// Low-level gateway for reading and writing values in a Dapr state store used as a cache.
/// Encapsulates store-name resolution, the shared <c>custom:</c> key namespace, TTL, consistency,
/// concurrency and ETag handling so callers (e.g. the cache-aside executor) do not talk to
/// <c>DaprClient</c> directly.
/// </summary>
public interface IStateStoreAccessor
{
    /// <summary>
    /// Resolves the effective store name: the supplied value, or the runtime's
    /// <c>DAPR_STATE_STORE_NAME</c> configuration when null/empty. Returns null when neither is available.
    /// </summary>
    string? ResolveStoreName(string? storeName);

    /// <summary>
    /// Applies the shared <c>custom:</c> namespace prefix to a caller-supplied key. Task-written cache
    /// entries share this prefix so they do not collide with engine-owned cache keys in the same store.
    /// </summary>
    string PrefixKey(string key);

    /// <summary>
    /// Reads a value from the state store.
    /// </summary>
    /// <param name="storeName">The resolved (non-empty) store name.</param>
    /// <param name="key">The caller-supplied key (prefixed internally).</param>
    /// <param name="consistency">Optional consistency mode (<c>Eventual</c>/<c>Strong</c>).</param>
    /// <param name="metadata">Optional additional Dapr operation metadata.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<StateGetResult> GetAsync(
        string storeName,
        string key,
        string? consistency,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a value to the state store.
    /// </summary>
    /// <param name="storeName">The resolved (non-empty) store name.</param>
    /// <param name="key">The caller-supplied key (prefixed internally).</param>
    /// <param name="value">The value to store.</param>
    /// <param name="ttlInSeconds">Optional TTL. When null or non-positive, no expiry is applied.</param>
    /// <param name="consistency">Optional consistency mode (<c>Eventual</c>/<c>Strong</c>).</param>
    /// <param name="concurrency">Optional concurrency mode (<c>FirstWrite</c>/<c>LastWrite</c>).</param>
    /// <param name="etag">Optional ETag for optimistic concurrency.</param>
    /// <param name="metadata">Optional additional Dapr operation metadata.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<StateSetResult> SetAsync(
        string storeName,
        string key,
        JsonElement value,
        int? ttlInSeconds,
        string? consistency,
        string? concurrency,
        string? etag,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a state store read.
/// </summary>
/// <param name="Found">Whether a non-null value was present under the key.</param>
/// <param name="Value">The value when found; otherwise the default (undefined) element.</param>
/// <param name="ETag">The entry's ETag, if any.</param>
public readonly record struct StateGetResult(bool Found, JsonElement Value, string? ETag);

/// <summary>
/// Result of a state store write.
/// </summary>
/// <param name="Saved">Whether the write succeeded (relevant when an ETag was supplied).</param>
public readonly record struct StateSetResult(bool Saved);
