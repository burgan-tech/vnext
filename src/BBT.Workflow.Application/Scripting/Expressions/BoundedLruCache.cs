namespace BBT.Workflow.Scripting.Expressions;

/// <summary>
/// Small thread-safe least-recently-used cache with a fixed capacity. Every read and write takes one
/// short lock (a dictionary lookup plus a linked-list move); values are produced by the caller outside
/// the lock. Not an <c>IMemoryCache</c> on purpose: its size-limit eviction is not deterministic LRU.
/// </summary>
/// <typeparam name="TKey">Key type.</typeparam>
/// <typeparam name="TValue">Value type.</typeparam>
public sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map;
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();
    private readonly object _gate = new();

    /// <summary>Creates a cache holding at most <paramref name="capacity"/> entries (minimum 1).</summary>
    public BoundedLruCache(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        _capacity = Math.Max(1, capacity);
        _map = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(comparer);
    }

    /// <summary>Number of entries currently cached.</summary>
    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    /// <summary>Returns the cached value and marks it most recently used.</summary>
    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
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
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _order.AddFirst(existing);
                return existing.Value.Value;
            }

            var node = new LinkedListNode<KeyValuePair<TKey, TValue>>(new KeyValuePair<TKey, TValue>(key, value));
            _order.AddFirst(node);
            _map[key] = node;
            if (_map.Count > _capacity)
            {
                var last = _order.Last!;
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
            }

            return value;
        }
    }
}
