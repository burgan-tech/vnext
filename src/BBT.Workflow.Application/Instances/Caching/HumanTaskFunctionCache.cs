using System.Security.Cryptography;
using System.Text;
using BBT.Aether.DistributedCache;
using BBT.Aether.Users;
using BBT.Workflow.Caching;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Instances.Caching;

/// <inheritdoc />
public sealed class HumanTaskFunctionCache(
    IDistributedCacheService cache,
    ICurrentUser currentUser,
    IOptions<HumanTaskFunctionCacheOptions> options,
    ILogger<HumanTaskFunctionCache> logger) : IHumanTaskFunctionCache
{
    private const string ComponentType = "human-task-fn";

    /// <summary>
    /// Response-shape generation. Bump it in the same commit as any change to what a row carries or
    /// to what the key covers — an entry written under the old meaning would otherwise be served
    /// under the new one for a whole TTL.
    /// </summary>
    private const string ResponseShapeVersion = "v2";

    private const string KeyPrefix = $"human-task:{ResponseShapeVersion}:";

    /// <inheritdoc />
    public bool Enabled => options.Value.Enabled;

    /// <inheritdoc />
    public bool AllowClientOverride => options.Value.AllowClientOverride;

    /// <inheritdoc />
    public string BuildKey(
        string domain,
        IReadOnlyList<string>? roles,
        IReadOnlyDictionary<string, string?>? headers)
    {
        var callerScope = CallerScopeHash.Compute(
            currentUser, role: null, roles: roles, extensions: null, headers: headers, version: null);

        return $"{KeyPrefix}{domain}:{callerScope}:{RequestHeaderHash.Compute(headers)}";
    }

    /// <inheritdoc />
    public async Task<HumanTaskFunctionCacheEntry?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationGet, key, ComponentType);

        try
        {
            var entry = await cache.GetAsync<HumanTaskFunctionCacheEntry>(key, cancellationToken);
            CacheActivityHelper.SetCacheHit(activity, entry is not null);
            return entry;
        }
        catch (Exception exception)
        {
            CacheActivityHelper.SetError(activity, exception);
            logger.HumanTaskFunctionCacheError(exception, CacheActivityHelper.OperationGet, key);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(
        string key,
        HumanTaskFunctionCacheEntry entry,
        CancellationToken cancellationToken = default)
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
                    AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(options.Value.TtlSeconds)
                },
                cancellationToken);
        }
        catch (Exception exception)
        {
            CacheActivityHelper.SetError(activity, exception);
            logger.HumanTaskFunctionCacheError(exception, CacheActivityHelper.OperationSet, key);
        }
    }
}
