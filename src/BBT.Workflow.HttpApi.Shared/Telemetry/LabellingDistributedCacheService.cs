using BBT.Aether.DistributedCache;
using BBT.Workflow.Logging;

namespace BBT.Workflow.HttpApi.Shared.Telemetry;

/// <summary>
/// Pass-through <see cref="IDistributedCacheService"/> decorator that publishes the cache key
/// into the <see cref="DaprCallLabel"/> ambient for the duration of each call, so
/// <see cref="DaprSpanLabelProcessor"/> can stamp it onto the Dapr gRPC client span the call
/// produces (GetState/SaveState/DeleteState). No behavior change — labelling only.
/// </summary>
public sealed class LabellingDistributedCacheService(IDistributedCacheService inner) : IDistributedCacheService
{
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class
    {
        using var scope = DaprCallLabel.Use(key);
        return await inner.GetAsync<T>(key, cancellationToken);
    }

    public async Task SetAsync<T>(
        string key,
        T value,
        DistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        using var scope = DaprCallLabel.Use(key);
        await inner.SetAsync(key, value, options, cancellationToken);
    }

    public async Task<T?> GetOrSetAsync<T>(
        string key,
        Func<Task<T>> factory,
        DistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        // The scope spans the factory too; a nested cache call re-labels via its own Use and the
        // dispose unwinds back to this key for the subsequent Set.
        using var scope = DaprCallLabel.Use(key);
        return await inner.GetOrSetAsync(key, factory, options, cancellationToken);
    }

    public async Task<T?> GetOrSetAsync<TKey, T>(
        TKey request,
        Func<TKey, Task<T>> factory,
        Func<TKey, string>? keyFactory = null,
        DistributedCacheEntryOptions? options = null,
        CancellationToken cancellationToken = default)
        where T : class
    {
        // keyFactory is evaluated once more for the label; the inner service derives the same key
        // from the same pure function. Without a keyFactory there is no key to label with.
        using var scope = keyFactory is null ? null : DaprCallLabel.Use(keyFactory(request));
        return await inner.GetOrSetAsync(request, factory, keyFactory, options, cancellationToken);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        using var scope = DaprCallLabel.Use(key);
        await inner.RemoveAsync(key, cancellationToken);
    }

    public async Task RefreshAsync(string key, CancellationToken cancellationToken = default)
    {
        using var scope = DaprCallLabel.Use(key);
        await inner.RefreshAsync(key, cancellationToken);
    }
}
