using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedCache;

namespace BBT.Workflow.Caching;

/// <summary>
/// In-memory <see cref="IDistributedCacheService"/> for cache tests.
/// </summary>
/// <remarks>
/// Hand-written rather than substituted because the behaviour under test is the <i>interaction between
/// keys</i> — which key a publish invalidates, which key a read then misses — and a per-call mock cannot
/// express that. Values are serialized on write and deserialized on read so envelope round-tripping
/// (which is why <see cref="CacheEnvelope{T}"/> exists at all) is exercised rather than bypassed.
/// </remarks>
public sealed class FakeDistributedCacheService(TimeProvider timeProvider) : IDistributedCacheService
{
    private readonly ConcurrentDictionary<string, Entry> _store = new();

    /// <summary>Keys read, in order, including misses.</summary>
    public List<string> Reads { get; } = [];

    /// <summary>Keys written, in order, including overwrites.</summary>
    public List<string> Writes { get; } = [];

    /// <summary>Keys removed, in order, including removals of absent keys.</summary>
    public List<string> Removes { get; } = [];

    /// <summary>When set, reads of matching keys throw, simulating an unreachable cache.</summary>
    public Func<string, bool>? FailReads { get; set; }

    /// <summary>When set, writes of matching keys throw, simulating an unreachable cache.</summary>
    public Func<string, bool>? FailWrites { get; set; }

    /// <summary>When set, removals of matching keys throw.</summary>
    public Func<string, bool>? FailRemoves { get; set; }

    /// <summary>Live (unexpired) keys currently held.</summary>
    public IReadOnlyList<string> Keys => _store
        .Where(kvp => !IsExpired(kvp.Value))
        .Select(kvp => kvp.Key)
        .OrderBy(k => k, StringComparer.Ordinal)
        .ToList();

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class
    {
        // Real cache services throw OperationCanceledException on a cancelled token; the fake has to
        // as well, or cancellation-propagation contracts cannot be tested against it.
        cancellationToken.ThrowIfCancellationRequested();

        lock (Reads)
        {
            Reads.Add(key);
        }

        if (FailReads?.Invoke(key) == true)
            throw new InvalidOperationException($"Simulated cache read failure for '{key}'.");

        if (!_store.TryGetValue(key, out var entry) || IsExpired(entry))
            return Task.FromResult<T?>(default);

        return Task.FromResult(JsonSerializer.Deserialize<T>(entry.Json));
    }

    public Task SetAsync<T>(
        string key,
        T value,
        DistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (Writes)
        {
            Writes.Add(key);
        }

        if (FailWrites?.Invoke(key) == true)
            throw new InvalidOperationException($"Simulated cache write failure for '{key}'.");

        _store[key] = new Entry(JsonSerializer.Serialize(value), options?.AbsoluteExpiration);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (Removes)
        {
            Removes.Add(key);
        }

        if (FailRemoves?.Invoke(key) == true)
            throw new InvalidOperationException($"Simulated cache remove failure for '{key}'.");

        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task RefreshAsync(string key, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async Task<T?> GetOrSetAsync<T>(
        string key,
        Func<Task<T>> factory,
        DistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        var existing = await GetAsync<T>(key, cancellationToken);
        if (existing is not null)
            return existing;

        var created = await factory();
        await SetAsync(key, created, options, cancellationToken);
        return created;
    }

    public async Task<TValue?> GetOrSetAsync<TKey, TValue>(
        TKey key,
        Func<TKey, Task<TValue>> factory,
        Func<TKey, string>? keySelector = null,
        DistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default)
        where TValue : class
    {
        var cacheKey = keySelector?.Invoke(key) ?? key?.ToString() ?? string.Empty;
        var existing = await GetAsync<TValue>(cacheKey, cancellationToken);
        if (existing is not null)
            return existing;

        var created = await factory(key);
        await SetAsync(cacheKey, created, options, cancellationToken);
        return created;
    }

    /// <summary>Gets the absolute expiry recorded for a key, or null when the key is absent.</summary>
    public DateTimeOffset? ExpiryOf(string key)
        => _store.TryGetValue(key, out var entry) ? entry.ExpiresAt : null;

    /// <summary>True when the key is present and holds no expiry at all.</summary>
    public bool HasNoExpiry(string key)
        => _store.TryGetValue(key, out var entry) && entry.ExpiresAt is null;

    /// <summary>Clears the recorded read/write/remove key lists without touching stored values.</summary>
    public void ClearLog()
    {
        lock (Reads)
        {
            Reads.Clear();
        }

        lock (Writes)
        {
            Writes.Clear();
        }

        lock (Removes)
        {
            Removes.Clear();
        }
    }

    private bool IsExpired(Entry entry)
        => entry.ExpiresAt is { } expiresAt && expiresAt <= timeProvider.GetUtcNow();

    private sealed record Entry(string Json, DateTimeOffset? ExpiresAt);
}
