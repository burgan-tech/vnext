using System.Text.Json;

namespace BBT.Workflow.Execution.StateStores;

/// <summary>
/// Shared gateway to a Dapr state store, used by both the State Store and Cache-Aside invokers.
/// Owns the single source of truth for store-name resolution, the <c>custom:</c> key namespace prefix,
/// consistency/concurrency parsing and TTL handling, so the two invokers cannot diverge (a cache-aside
/// entry and a state-store entry for the same logical key always hit the same physical record).
/// </summary>
public interface IStateStoreClient
{
    /// <summary>
    /// Resolves the effective store name: the supplied value, or the runtime's
    /// <c>DAPR_STATE_STORE_NAME</c> configuration when null/empty. Returns null when neither is available.
    /// </summary>
    string? ResolveStoreName(string? storeName);

    /// <summary>
    /// Applies the shared <c>custom:</c> namespace prefix to a caller-supplied key.
    /// </summary>
    string PrefixKey(string key);

    /// <summary>
    /// Reads a value. The <paramref name="key"/> is prefixed internally.
    /// </summary>
    Task<StateEntry> GetAsync(
        string storeName,
        string key,
        string? consistency,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a value. The <paramref name="key"/> is prefixed internally; a positive
    /// <paramref name="ttlInSeconds"/> is applied as Dapr <c>ttlInSeconds</c> metadata. When an
    /// <paramref name="etag"/> is supplied an optimistic write is used and the returned flag reports
    /// whether it succeeded; otherwise the write is unconditional and returns <c>true</c>.
    /// </summary>
    Task<bool> SetAsync<TValue>(
        string storeName,
        string key,
        TValue value,
        int? ttlInSeconds,
        string? consistency,
        string? concurrency,
        string? etag,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a single value. The <paramref name="key"/> is prefixed internally.
    /// </summary>
    Task DeleteAsync(
        string storeName,
        string key,
        string? consistency,
        string? concurrency,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk-deletes the supplied keys as-is (callers pass already-prefixed keys).
    /// Returns the number of keys submitted for deletion.
    /// </summary>
    Task<int> DeleteBulkAsync(
        string storeName,
        IReadOnlyList<string> prefixedKeys,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs a Dapr state Query API filter and returns the matched keys that belong to the
    /// <c>custom:</c> namespace (already prefixed, safe to bulk-delete as-is).
    /// </summary>
    Task<IReadOnlyList<string>> QueryPrefixedKeysAsync(
        string storeName,
        string query,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a state store read.
/// </summary>
/// <param name="Found">Whether a non-null value was present under the key.</param>
/// <param name="Value">The value when found; otherwise the default (undefined) element.</param>
/// <param name="ETag">The entry's ETag, if any.</param>
public readonly record struct StateEntry(bool Found, JsonElement Value, string? ETag);
