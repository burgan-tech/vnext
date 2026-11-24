using System.Collections.Concurrent;

namespace BBT.Workflow.Scripting.Expressions;

/// <summary>
/// Small thread-safe least-recently-used cache with a fixed capacity. A hit takes no lock: a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> lookup plus one recency stamp from a shared counter
/// (<see cref="Interlocked.Increment(ref long)"/>). Only an insert takes the lock, and an insert that overflows
/// evicts the entry with the oldest stamp (a scan of at most capacity entries — paid on a miss, next to a compile).
/// Values are produced by the caller outside the lock. Not an <c>IMemoryCache</c> on purpose: its size-limit
/// eviction is not deterministic LRU.
/// </summary>
/// <remarks>
/// Single-threaded use is exact LRU (stamps are strictly increasing). Under concurrency it is approximate: a hit that
/// races an eviction scan may lose its entry although it was just used, which only costs a recompile. The capacity
/// bound is exact — every insert and every removal happens under the lock.
/// </remarks>
/// <typeparam name="TKey">Key type.</typeparam>
/// <typeparam name="TValue">Value type.</typeparam>
public sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly ConcurrentDictionary<TKey, Entry> _map;
    private readonly object _gate = new();
    private long _clock;

    /// <summary>Creates a cache holding at most <paramref name="capacity"/> entries (minimum 1).</summary>
    public BoundedLruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        _capacity = Math.Max(1, capacity);
        _map = new ConcurrentDictionary<TKey, Entry>(comparer ?? EqualityComparer<TKey>.Default);
    }

    /// <summary>Number of entries currently cached.</summary>
    public int Count => _map.Count;

    /// <summary>Returns the cached value and marks it most recently used. Lock-free.</summary>
    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var entry))
        {
            Touch(entry);
            value = entry.Value;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Adds the value unless the key is already present; returns the value that is now cached (the
    /// existing one when another caller won the race), evicting the least recently used entry on overflow.
    /// </summary>
    public TValue GetOrAdd(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out var existing))
        {
            Touch(existing);
            return existing.Value;
        }

        lock (_gate)
        {
            if (_map.TryGetValue(key, out existing))
            {
                Touch(existing);
                return existing.Value;
            }

            _map[key] = new Entry(value, Interlocked.Increment(ref _clock));
            while (_map.Count > _capacity && EvictOldest(key))
            {
            }

            return value;
        }
    }

    // An entry that already holds the newest stamp is already the most recent: skip the shared-counter write, so a
    // hot key read from many threads does not bounce the counter's cache line.
    private void Touch(Entry entry)
    {
        if (Volatile.Read(ref entry.Stamp) != Volatile.Read(ref _clock))
            Volatile.Write(ref entry.Stamp, Interlocked.Increment(ref _clock));
    }

    // Under _gate. Removes the entry with the smallest stamp, never the one just inserted.
    private bool EvictOldest(TKey inserted)
    {
        var found = false;
        TKey oldestKey = default!;
        var oldestStamp = long.MaxValue;
        foreach (var (key, entry) in _map)
        {
            var stamp = Volatile.Read(ref entry.Stamp);
            if (stamp < oldestStamp && !_map.Comparer.Equals(key, inserted))
            {
                oldestStamp = stamp;
                oldestKey = key;
                found = true;
            }
        }

        return found && _map.TryRemove(oldestKey, out _);
    }

    private sealed class Entry(TValue value, long stamp)
    {
        public readonly TValue Value = value;
        public long Stamp = stamp;
    }
}
