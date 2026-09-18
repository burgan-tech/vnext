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
    private const string ResponseShapeVersion = "v1";

    private const string KeyPrefix = $"human-task:{ResponseShapeVersion}:";

    /// <summary>
    /// Headers excluded from the key because they vary per request while no authorization decision
    /// can depend on them. Everything else is folded in, deliberately erring towards a lower hit
    /// rate rather than towards serving one caller scope's list to another — a dynamic role grant
    /// may read any header name the workflow author picked, so the set that matters cannot be
    /// enumerated here. This list is the tuning knob if the hit rate proves too low; widening it is
    /// a security decision, not a performance one.
    /// </summary>
    private static readonly HashSet<string> VolatileHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "traceparent", "tracestate", "baggage",
        "x-request-id", "x-correlation-id", "request-id",
        "date", "user-agent", "content-length", "accept-encoding", "connection"
    };

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

        return $"{KeyPrefix}{domain}:{callerScope}:{HashAuthorizationInputs(headers)}";
    }

    /// <summary>
    /// Hashes the headers a dynamic role grant could read. <c>CallerScopeHash</c> covers role,
    /// identity and culture; it does not cover <c>$.context.Headers.*</c>, and a grant evaluated
    /// against different headers can reach a different answer for the same roles.
    /// </summary>
    private static string HashAuthorizationInputs(IReadOnlyDictionary<string, string?>? headers)
    {
        if (headers is null || headers.Count == 0)
            return "0";

        var builder = new StringBuilder();
        foreach (var header in headers
                     .Where(h => !VolatileHeaders.Contains(h.Key))
                     .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(header.Key.ToLowerInvariant()).Append('=').Append(header.Value).Append('|');
        }

        if (builder.Length == 0)
            return "0";

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];
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
