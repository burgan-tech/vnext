using System.Text.Json;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
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
    private const string OperationL1Set = "Cache.L1Set";

    private readonly MemoryCache? _cache;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ComponentL1Cache> _logger;

    public ComponentL1Cache(
        IOptions<ComponentCacheOptions> options,
        TimeProvider timeProvider,
        ILogger<ComponentL1Cache> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
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

        // Expirations are produced by the caller's TimeProvider while MemoryCache runs on its own
        // clock, so convert to a relative TTL — "valid this long from now" survives the difference.
        var timeToLive = absoluteExpiration - _timeProvider.GetUtcNow();
        if (timeToLive <= TimeSpan.Zero)
            return;

        byte[] bytes;
        try
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonSerializerConstants.JsonOptions);
        }
        catch (Exception ex)
        {
            // Serialization walks entity getters, which may throw on partially populated instances.
            // L1 is an optimization: skipping the write is always correct, failing the caller never is.
            _logger.ComponentCacheOperationFailed(ex, OperationL1Set, cacheKey);
            return;
        }

        _cache.Set(cacheKey, bytes, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = timeToLive,
            Size = bytes.LongLength
        });
    }

    /// <inheritdoc />
    public void Remove(string cacheKey) => _cache?.Remove(cacheKey);

    public void Dispose() => _cache?.Dispose();
}
