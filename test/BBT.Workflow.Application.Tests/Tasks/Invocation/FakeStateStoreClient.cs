using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution.Core.StateStores;

namespace BBT.Workflow.Application.Tests.Tasks.Invocation;

/// <summary>
/// In-memory <see cref="IStateStoreClient"/> test double for the in-process state-store invoker
/// tests (<see cref="LocalStateStoreTaskInvokerTests"/>). Mirrors <see cref="DaprStateStoreClient"/>'s
/// observable contract closely enough for those tests: the <c>custom:</c> key namespace prefix and
/// store-name resolution (explicit value, else the configured default).
/// </summary>
internal sealed class FakeStateStoreClient(string? defaultStoreName) : IStateStoreClient
{
    private readonly Dictionary<string, (JsonElement Value, string ETag)> _entries = new();
    private int _etagSequence;

    /// <summary>Seeds a value under a logical (unprefixed) key, as a test would author it.</summary>
    public void Seed(string key, JsonElement value, string? etag = null)
    {
        _entries[PrefixKey(key)] = (value, etag ?? $"etag-{++_etagSequence}");
    }

    /// <summary>Whether a value is currently stored under a logical (unprefixed) key.</summary>
    public bool Contains(string key) => _entries.ContainsKey(PrefixKey(key));

    /// <summary>
    /// When set, <see cref="GetAsync"/> throws this instead of reading — simulates a state-store
    /// outage for the cache-aside <c>bypassOnCacheError</c> tests.
    /// </summary>
    public Exception? ThrowOnGet { get; set; }

    /// <inheritdoc />
    public string? ResolveStoreName(string? storeName) =>
        !string.IsNullOrWhiteSpace(storeName) ? storeName : defaultStoreName;

    /// <inheritdoc />
    public string PrefixKey(string key) => "custom:" + key;

    /// <inheritdoc />
    public Task<StateEntry> GetAsync(
        string storeName,
        string key,
        string? consistency,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default)
    {
        if (ThrowOnGet is { } ex)
        {
            throw ex;
        }

        return Task.FromResult(_entries.TryGetValue(PrefixKey(key), out var entry)
            ? new StateEntry(true, entry.Value, entry.ETag)
            : new StateEntry(false, default, null));
    }

    /// <inheritdoc />
    public Task<bool> SetAsync<TValue>(
        string storeName,
        string key,
        TValue value,
        int? ttlInSeconds,
        string? consistency,
        string? concurrency,
        string? etag,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default)
    {
        _entries[PrefixKey(key)] = (JsonSerializer.SerializeToElement(value), $"etag-{++_etagSequence}");
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task DeleteAsync(
        string storeName,
        string key,
        string? consistency,
        string? concurrency,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default)
    {
        _entries.Remove(PrefixKey(key));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> DeleteBulkAsync(
        string storeName,
        IReadOnlyList<string> prefixedKeys,
        CancellationToken cancellationToken = default)
    {
        var deleted = prefixedKeys.Count(key => _entries.Remove(key));
        return Task.FromResult(deleted);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> QueryPrefixedKeysAsync(
        string storeName,
        string query,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> keys = _entries.Keys.ToList();
        return Task.FromResult(keys);
    }
}
