using System.Security.Cryptography;
using System.Text;
using BBT.Aether.DistributedCache;
using BBT.Aether.Users;
using BBT.Workflow.Caching;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// Distributed-cache implementation of <see cref="IDataFunctionCache"/> backed by
/// <see cref="IDistributedCacheService"/>. Entry TTL is resolved per workflow by the caller
/// (flow definition's <c>functionCache.ttlSeconds</c>, falling back to
/// <see cref="InstanceFunctionCacheOptions.DefaultTtlSeconds"/>); data freshness within the
/// TTL is guaranteed by the fingerprint-ETag validation, not by this store.
/// All cache failures degrade to a miss — a broken cache must never fail a data request.
/// </summary>
public sealed class DataFunctionCache(
    IDistributedCacheService cache,
    ICurrentUser currentUser,
    IOptions<InstanceFunctionCacheOptions> options,
    ILogger<DataFunctionCache> logger) : IDataFunctionCache
{
    private const string ComponentType = "data-fn";
    private const string KeyPrefix = "data-fn:";

    /// <summary>
    /// Length of the fingerprint ETag (hex chars of the SHA-256 digest — 128 bits).
    /// </summary>
    private const int EtagLength = 32;

    /// <inheritdoc />
    public bool Enabled => options.Value.Enabled;

    /// <inheritdoc />
    public int ResolveTtlSeconds(Definitions.FunctionCacheDefinition? functionCache) =>
        functionCache?.TtlSeconds is > 0 ? functionCache.TtlSeconds.Value : options.Value.DefaultTtlSeconds;

    /// <inheritdoc />
    public string BuildKey(GetInstanceDataInput input) =>
        $"{KeyPrefix}{input.Domain}:{input.Workflow}:{input.Instance}:{BuildCallerHash(input)}";

    /// <inheritdoc />
    public string ComputeEtag(GetInstanceDataInput input, InstanceDataFingerprint fingerprint)
    {
        var material = string.Join('|',
            fingerprint.Id,
            fingerprint.LatestDataEtag ?? string.Empty,
            fingerprint.FlowVersion ?? string.Empty,
            BuildCallerHash(input));

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..EtagLength];
    }

    // Extensions are deliberately EXCLUDED from the caller scope: the ETag tracks the data
    // change point only (extension flux never moves it), and requests differing only in the
    // requested extension list share one key/ETag — the body cache serves extensionless
    // requests, extension-carrying requests are always rebuilt fresh.
    private string BuildCallerHash(GetInstanceDataInput input) =>
        CallerScopeHash.Compute(currentUser, role: null, input.Roles, extensions: null, input.Headers, input.Version);

    /// <inheritdoc />
    public async Task<DataFunctionCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationGet, key, ComponentType);

        try
        {
            var entry = await cache.GetAsync<DataFunctionCacheEntry>(key, cancellationToken);
            CacheActivityHelper.SetCacheHit(activity, entry is not null);
            return entry;
        }
        catch (Exception ex)
        {
            CacheActivityHelper.SetError(activity, ex);
            logger.DataFunctionCacheError(ex, CacheActivityHelper.OperationGet, key);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, DataFunctionCacheEntry entry, int ttlSeconds, CancellationToken cancellationToken = default)
    {
        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationSet, key, ComponentType);

        try
        {
            await cache.SetAsync(
                key,
                entry,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(ttlSeconds)
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            CacheActivityHelper.SetError(activity, ex);
            logger.DataFunctionCacheError(ex, CacheActivityHelper.OperationSet, key);
        }
    }
}
