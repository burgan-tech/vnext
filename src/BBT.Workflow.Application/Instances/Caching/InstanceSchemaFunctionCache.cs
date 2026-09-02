using System.Security.Cryptography;
using System.Text;
using BBT.Aether.DistributedCache;
using BBT.Aether.Users;
using BBT.Workflow.Caching;
using BBT.Workflow.Instances.DTOs;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// Distributed-cache implementation of <see cref="IInstanceSchemaFunctionCache"/> backed by
/// <see cref="IDistributedCacheService"/>, serving both the master and the schema functions.
/// Entry TTL is resolved per workflow by the caller (flow definition's
/// <c>functionCache.ttlSeconds</c>, falling back to
/// <see cref="InstanceFunctionCacheOptions.DefaultTtlSeconds"/>). All cache failures degrade
/// to a miss — a broken cache must never fail a request.
/// </summary>
public sealed class InstanceSchemaFunctionCache(
    IDistributedCacheService cache,
    ICurrentUser currentUser,
    IOptions<InstanceFunctionCacheOptions> options,
    ILogger<InstanceSchemaFunctionCache> logger) : IInstanceSchemaFunctionCache
{
    /// <summary>
    /// The <c>v1</c> segment is a cache generation, not a response-shape version: bump it whenever a
    /// change alters what a cached body means for a given caller hash. It was introduced to retire
    /// entries written before caller roles became provider-resolved.
    /// </summary>
    internal const string MasterKeyPrefix = "master-fn:v1:";

    /// <inheritdoc cref="MasterKeyPrefix" />
    internal const string SchemaKeyPrefix = "schema-fn:v1:";

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
    public string BuildKey(GetMasterInput input) =>
        $"{MasterKeyPrefix}{input.Domain}:{input.Workflow}:{input.Instance}:{BuildCallerHash(input.Roles, input.Headers, input.Version)}";

    /// <inheritdoc />
    public string BuildKey(GetSchemaInput input, string transitionKey) =>
        $"{SchemaKeyPrefix}{input.Domain}:{input.Workflow}:{input.Instance}:{BuildCallerHash(input.Roles, input.Headers, input.Version)}:{transitionKey}";

    /// <inheritdoc />
    public string ComputeEtag(GetMasterInput input, InstanceDataFingerprint fingerprint) =>
        HashEtag(string.Join('|',
            fingerprint.Id,
            fingerprint.LatestDataEtag ?? string.Empty,
            fingerprint.FlowVersion ?? string.Empty,
            BuildCallerHash(input.Roles, input.Headers, input.Version)));

    /// <inheritdoc />
    public string ComputeEtag(GetSchemaInput input, InstanceDataFingerprint fingerprint, string transitionKey) =>
        HashEtag(string.Join('|',
            fingerprint.Id,
            fingerprint.LatestDataEtag ?? string.Empty,
            fingerprint.EffectiveState ?? string.Empty,
            fingerprint.FlowVersion ?? string.Empty,
            BuildCallerHash(input.Roles, input.Headers, input.Version),
            transitionKey));

    // Extensions play no role in the master/schema functions — the caller scope is
    // roles + actor identity + culture + requested workflow version only.
    private string BuildCallerHash(
        IReadOnlyList<string>? roles,
        IReadOnlyDictionary<string, string?>? headers,
        string? version) =>
        CallerScopeHash.Compute(currentUser, role: null, roles, extensions: null, headers, version);

    private static string HashEtag(string material) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..EtagLength];

    /// <inheritdoc />
    public async Task<SchemaFunctionCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationGet, key, ComponentTypeOf(key));

        try
        {
            var entry = await cache.GetAsync<SchemaFunctionCacheEntry>(key, cancellationToken);
            CacheActivityHelper.SetCacheHit(activity, entry is not null);
            return entry;
        }
        catch (Exception ex)
        {
            CacheActivityHelper.SetError(activity, ex);
            logger.InstanceSchemaFunctionCacheError(ex, ComponentTypeOf(key), CacheActivityHelper.OperationGet, key);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, SchemaFunctionCacheEntry entry, int ttlSeconds, CancellationToken cancellationToken = default)
    {
        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationSet, key, ComponentTypeOf(key));

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
            logger.InstanceSchemaFunctionCacheError(ex, ComponentTypeOf(key), CacheActivityHelper.OperationSet, key);
        }
    }

    private static string ComponentTypeOf(string key) =>
        key.StartsWith(MasterKeyPrefix, StringComparison.Ordinal) ? "master-fn" : "schema-fn";
}
