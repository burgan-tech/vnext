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
/// Distributed-cache implementation of <see cref="IStateFunctionCache"/> backed by
/// <see cref="IDistributedCacheService"/>. Entries expire after the configured TTL
/// (default 60s — the client long-poll timeout); state/status freshness within the TTL
/// is guaranteed by the caller's fingerprint-ETag validation, not by this store.
/// All cache failures degrade to a miss — a broken cache must never fail a poll request.
/// </summary>
public sealed class StateFunctionCache(
    IDistributedCacheService cache,
    ICurrentUser currentUser,
    IOptions<StateFunctionCacheOptions> options,
    ILogger<StateFunctionCache> logger) : IStateFunctionCache
{
    private const string ComponentType = "state-fn";
    private const string KeyPrefix = "state-fn:";

    /// <summary>
    /// Length of the caller-scope hash segment in the cache key (hex chars of the SHA-256 digest).
    /// </summary>
    private const int CallerHashLength = 16;

    /// <summary>
    /// Length of the fingerprint ETag (hex chars of the SHA-256 digest — 128 bits).
    /// </summary>
    private const int EtagLength = 32;

    /// <inheritdoc />
    public bool Enabled => options.Value.Enabled;

    /// <inheritdoc />
    public string BuildKey(GetInstanceStateInput input) =>
        $"{KeyPrefix}{input.Domain}:{input.Workflow}:{input.Instance}:{BuildCallerHash(input)}";

    /// <inheritdoc />
    public string ComputeEtag(GetInstanceStateInput input, InstanceStateFingerprint fingerprint) =>
        ComputeEtagCore(input, fingerprint, displayedState: null, displayedStatus: null);

    /// <inheritdoc />
    public string ComputeEtag(GetInstanceStateInput input, InstanceStateFingerprint fingerprint,
        GetInstanceStateOutput subFlowOutput) =>
        ComputeEtagCore(input, fingerprint, subFlowOutput.State, subFlowOutput.Status?.Code);

    private string ComputeEtagCore(
        GetInstanceStateInput input,
        InstanceStateFingerprint fingerprint,
        string? displayedState,
        string? displayedStatus)
    {
        var material = string.Join('|',
            fingerprint.Id,
            fingerprint.EffectiveState ?? string.Empty,
            fingerprint.Status.Code,
            fingerprint.FlowVersion ?? string.Empty,
            BuildCallerHash(input),
            displayedState ?? string.Empty,
            displayedStatus ?? string.Empty);

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..EtagLength];
    }

    /// <summary>
    /// Hashes the caller scope: role/roles, actor identity ($InstanceStarter/$PreviousUser
    /// pseudo-roles are matched against ICurrentUser — see TransitionAuthorizationManager, so two
    /// callers with identical role headers can receive different transition lists), resolved
    /// culture (state alias labels are localized), requested extensions and workflow version.
    /// </summary>
    private string BuildCallerHash(GetInstanceStateInput input)
    {
        var roles = input.Roles is { Count: > 0 }
            ? string.Join(',', input.Roles.Order(StringComparer.Ordinal))
            : string.Empty;
        var extensions = input.Extensions is { Length: > 0 }
            ? string.Join(',', input.Extensions.Order(StringComparer.Ordinal))
            : string.Empty;
        var culture = LanguageResolver.ResolveCulture(input.Headers);

        var callerScope = string.Join('|',
            input.Role ?? string.Empty,
            roles,
            currentUser.Id ?? string.Empty,
            currentUser.ActorUserName ?? string.Empty,
            culture,
            extensions,
            input.Version ?? string.Empty);

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(callerScope)))[..CallerHashLength];
    }

    /// <inheritdoc />
    public async Task<StateFunctionCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationGet, key, ComponentType);

        try
        {
            var entry = await cache.GetAsync<StateFunctionCacheEntry>(key, cancellationToken);
            CacheActivityHelper.SetCacheHit(activity, entry is not null);
            return entry;
        }
        catch (Exception ex)
        {
            CacheActivityHelper.SetError(activity, ex);
            logger.StateFunctionCacheError(ex, CacheActivityHelper.OperationGet, key);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, StateFunctionCacheEntry entry, CancellationToken cancellationToken = default)
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
        catch (Exception ex)
        {
            CacheActivityHelper.SetError(activity, ex);
            logger.StateFunctionCacheError(ex, CacheActivityHelper.OperationSet, key);
        }
    }
}
