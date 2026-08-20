using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Caching;

/// <summary>
/// Bytes-mode <see cref="IComponentL1Cache"/> backed by a private size-limited
/// <see cref="MemoryCache"/> shared across all component types in the process.
/// </summary>
/// <remarks>
/// Entries are the envelope serialized with <see cref="JsonSerializerConstants.JsonOptions"/> and
/// sized by byte length, so <see cref="ComponentCacheOptions.L1SizeLimitMb"/> bounds real payload
/// memory. A private cache instance is used, not the DI <c>IMemoryCache</c>, to keep the
/// "distributed cache for business data" rule intact everywhere else.
/// </remarks>
public sealed class ComponentL1Cache : IComponentL1Cache
{
    private readonly MemoryCache? _cache;

    public ComponentL1Cache(IOptions<ComponentCacheOptions> options)
    {
        if (options.Value.L1Enabled)
        {
            _cache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = (long)options.Value.L1SizeLimitMb * 1024 * 1024
            });
        }
    }

    /// <inheritdoc />
    public CacheEnvelope<T>? TryGet<T>(string cacheKey) where T : class
    {
        if (_cache is null || !_cache.TryGetValue(cacheKey, out byte[]? bytes) || bytes is null)
            return null;

        try
        {
            return JsonSerializer.Deserialize<CacheEnvelope<T>>(bytes, JsonSerializerConstants.JsonOptions);
        }
        catch (JsonException)
        {
            // A poisoned entry must not poison the read path; drop it and fall through to L2.
            _cache.Remove(cacheKey);
            return null;
        }
    }

    /// <inheritdoc />
    public void Set<T>(string cacheKey, CacheEnvelope<T> envelope, DateTimeOffset absoluteExpiration) where T : class
    {
        if (_cache is null || envelope.IsNegative || envelope.Entity is null)
            return;

        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonSerializerConstants.JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return;
        }

        _cache.Set(cacheKey, bytes, new MemoryCacheEntryOptions
        {
            AbsoluteExpiration = absoluteExpiration,
            Size = bytes.LongLength
        });
    }

    /// <inheritdoc />
    public void Remove(string cacheKey) => _cache?.Remove(cacheKey);

    public void Dispose() => _cache?.Dispose();
}
