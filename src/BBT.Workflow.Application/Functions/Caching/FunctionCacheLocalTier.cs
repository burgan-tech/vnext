using System.Collections.Concurrent;
using System.Text.Json;
using BBT.Aether.Results;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Functions.Caching;

/// <summary>
/// Per-pod tier in front of the function cache's shared state store: a short-lived L1 copy of generation
/// stamps and responses, and single-flight coalescing of concurrent misses for one key.
/// </summary>
public interface IFunctionCacheLocalTier
{
    /// <summary>True when the L1 copy is enabled (<see cref="FunctionCacheOptions.LocalTtlMilliseconds"/> &gt; 0).</summary>
    bool LocalEnabled { get; }

    /// <summary>Reads a generation stamp held for <paramref name="generationKey"/> in <paramref name="storeName"/>.</summary>
    bool TryGetGeneration(string? storeName, string generationKey, out string generation);

    /// <summary>Holds a generation stamp read from the state store for the L1 lifetime.</summary>
    void SetGeneration(string? storeName, string generationKey, string generation);

    /// <summary>Reads a response held for the full cache key, as a fresh copy.</summary>
    FunctionResponseOutput? TryGetResponse(string? storeName, string cacheKey);

    /// <summary>Holds a response (cache hit or freshly computed) for the L1 lifetime.</summary>
    void SetResponse(string? storeName, string cacheKey, FunctionResponseOutput response);

    /// <summary>
    /// Runs <paramref name="execute"/> once per key among concurrent callers in this pod. A caller that
    /// joins an in-flight execution gets a copy of its successful result; if that execution fails or is
    /// cancelled, the joining caller runs <paramref name="execute"/> itself.
    /// </summary>
    Task<Result<FunctionResponseOutput>> RunOnceAsync(
        string cacheKey,
        Func<Task<Result<FunctionResponseOutput>>> execute,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IFunctionCacheLocalTier"/>. Singleton. Responses are kept as serialized bytes so
/// every reader gets its own copy — the same shape a state-store hit deserializes into — and nothing is
/// shared mutably between requests. A private size-limited <see cref="MemoryCache"/> is used (as
/// <c>ComponentL1Cache</c> does) rather than the DI <c>IMemoryCache</c>.
/// </summary>
/// <remarks>
/// Invalidation: entries live at most <see cref="FunctionCacheOptions.LocalTtlMilliseconds"/>. A response
/// entry is keyed by the full cache key, which already carries the generation stamp, so a generation bump
/// reaches a pod no later than its memoized stamp expires.
/// </remarks>
public sealed class FunctionCacheLocalTier : IFunctionCacheLocalTier, IDisposable
{
    private readonly MemoryCache? _cache;
    private readonly TimeSpan _ttl;
    private readonly bool _singleFlight;
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _inFlight = new(StringComparer.Ordinal);

    public FunctionCacheLocalTier(IOptions<FunctionCacheOptions> options)
    {
        var value = options.Value;
        _singleFlight = value.SingleFlight;
        _ttl = TimeSpan.FromMilliseconds(value.LocalTtlMilliseconds);
        if (value.LocalTtlMilliseconds > 0)
        {
            _cache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = (long)value.LocalSizeLimitMb * 1024 * 1024
            });
        }
    }

    /// <inheritdoc />
    public bool LocalEnabled => _cache is not null;

    /// <inheritdoc />
    public bool TryGetGeneration(string? storeName, string generationKey, out string generation)
    {
        generation = string.Empty;
        if (_cache is null || !_cache.TryGetValue(GenerationEntry(storeName, generationKey), out string? held) || held is null)
            return false;
        generation = held;
        return true;
    }

    /// <inheritdoc />
    public void SetGeneration(string? storeName, string generationKey, string generation)
    {
        if (_cache is null)
            return;
        _cache.Set(GenerationEntry(storeName, generationKey), generation, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _ttl,
            Size = generation.Length + generationKey.Length
        });
    }

    /// <inheritdoc />
    public FunctionResponseOutput? TryGetResponse(string? storeName, string cacheKey)
    {
        if (_cache is null || !_cache.TryGetValue(ResponseEntry(storeName, cacheKey), out byte[]? bytes) || bytes is null)
            return null;
        return Deserialize(bytes);
    }

    /// <inheritdoc />
    public void SetResponse(string? storeName, string cacheKey, FunctionResponseOutput response)
    {
        if (_cache is null)
            return;
        var bytes = Serialize(response);
        if (bytes is null)
            return;
        _cache.Set(ResponseEntry(storeName, cacheKey), bytes, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _ttl,
            Size = bytes.Length
        });
    }

    /// <inheritdoc />
    public async Task<Result<FunctionResponseOutput>> RunOnceAsync(
        string cacheKey,
        Func<Task<Result<FunctionResponseOutput>>> execute,
        CancellationToken cancellationToken)
    {
        if (!_singleFlight)
            return await execute();

        var leader = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = _inFlight.GetOrAdd(cacheKey, leader.Task);
        if (!ReferenceEquals(inFlight, leader.Task))
        {
            // Follower: share the leader's successful result; anything else (failure, cancellation of the
            // leader's request) and this caller runs the function itself.
            try
            {
                var shared = await inFlight.WaitAsync(cancellationToken);
                if (shared is not null && Deserialize(shared) is { } copy)
                    return Result<FunctionResponseOutput>.Ok(copy);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // The leader faulted; fall through and execute independently.
            }

            return await execute();
        }

        try
        {
            var result = await execute();
            leader.TrySetResult(result.IsSuccess && result.Value is not null ? Serialize(result.Value) : null);
            return result;
        }
        catch (Exception ex)
        {
            leader.TrySetException(ex);
            throw;
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Task<byte[]?>>(cacheKey, leader.Task));
        }
    }

    /// <inheritdoc />
    public void Dispose() => _cache?.Dispose();

    private static string GenerationEntry(string? storeName, string generationKey) => $"g|{storeName}|{generationKey}";

    private static string ResponseEntry(string? storeName, string cacheKey) => $"r|{storeName}|{cacheKey}";

    private static byte[]? Serialize(FunctionResponseOutput response)
    {
        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(response, JsonSerializerConstants.JsonOptions);
        }
        catch (Exception)
        {
            // An unserializable response is simply not shared; the caller still returns its own result.
            return null;
        }
    }

    private static FunctionResponseOutput? Deserialize(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<FunctionResponseOutput>(bytes, JsonSerializerConstants.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
