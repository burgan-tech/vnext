using System.Collections.Concurrent;
using System.Diagnostics;
using BBT.Aether.DistributedCache;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Caching;

/// <summary>
/// Redis-first cache implementation with DB fallback. No in-memory snapshot.
/// All pods share the same Redis instance, so writes are immediately visible cluster-wide.
/// </summary>
/// <remarks>
/// Two kinds of entry are stored, and the difference between them is the whole design:
/// <list type="bullet">
///     <item><description>
///         <c>full:{canonicalVersion}</c> — a component body keyed by its full version. A full version
///         names one revision forever, so this may be written unconditionally and reused indefinitely.
///     </description></item>
///     <item><description>
///         <c>res:{generation}:{spelling}</c> — the answer to a version <i>request</i>
///         (<c>latest</c>, <c>1</c>, <c>1.2</c>, <c>2.3.5</c>). Its value depends on the set of
///         published versions, not on one revision, so publishing or deactivating anything can change
///         it. These are never written as though the published entity wins the range; instead they are
///         scoped to a generation token that is replaced whenever the published set changes.
///     </description></item>
/// </list>
/// Keying resolutions under a generation is what makes invalidation complete. A publish cannot enumerate
/// which request spellings its component participates in — <see cref="InstanceDataVersionComparer.FindBestMatch"/>
/// also accepts leading-zero package-version aliases, and could accept more forms later — so instead of
/// listing them, replacing the token makes all of them unreachable at once.
/// </remarks>
/// <typeparam name="T">The type of entity to cache</typeparam>
public class CacheSet<T>(
    IDistributedCacheService distributedCache,
    ICacheBackend<T> backend,
    IComponentGenerationProvider generationProvider,
    IOptions<ComponentCacheOptions> options,
    TimeProvider timeProvider,
    ILogger<CacheSet<T>> logger)
    : ICacheSet<T>
    where T : class, IDomainEntity, IReferenceSetter
{
    private static readonly string ComponentKeyName = T.ComponentTypeKey;

    /// <summary>
    /// Resolutions in flight, so concurrent misses for the same component and spelling share one
    /// backend load instead of each issuing their own. Misses cluster immediately after a publish, which
    /// is exactly when a hot component would otherwise stampede the database.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<Result<T>>>> _inFlightResolutions = new();

    public Type EntityType => typeof(T);

    /// <inheritdoc />
    public async Task<Result<T>> GetByVersionAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default)
    {
        if (InstanceDataVersionComparer.IsFullVersion(version))
            return await GetFullVersionAsync(domain, key, version!, cancellationToken);

        return await GetResolvedAsync(domain, key, version, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<T>> GetLatestByNameAsync(
        string domain,
        string name,
        CancellationToken cancellationToken = default)
    {
        return await GetResolvedAsync(domain, name, InstanceDataVersionComparer.LatestKeyword, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result> SetAsync(T entity, CancellationToken cancellationToken = default)
    {
        if (entity is null)
            return Result.Fail(CacheErrors.EntityCannotBeNull());

        var domain = entity.Domain;
        var key = entity.Key;
        var fullVersion = entity.Version;

        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationSet, CreateFullKey(domain, key, fullVersion), ComponentKeyName);

        // 1) The body under its immutable full-version key. Safe to overwrite: the key names this exact
        //    revision, so the only thing an overwrite can change is a re-published body.
        await TryWriteAsync(
            CreateFullKey(domain, key, fullVersion),
            CreateEnvelope(entity),
            FullEntryOptions(),
            cancellationToken);

        // 2) Invalidate every cached version resolution for this component. Not just the ones derivable
        //    from this version: adding a version can change what any request resolves to, and this
        //    publish may well be a lower version than what is already stored.
        var generation = await generationProvider.BumpAsync(ComponentKeyName, domain, key, cancellationToken);
        CacheActivityHelper.SetGeneration(activity, generation);

        // 3) Re-resolve the common spellings and cache them under the new generation, so a deploy does
        //    not leave every hot component to be resolved by whichever request arrives first.
        await WarmResolutionsAsync(domain, key, entity, generation, activity, cancellationToken);

        if (options.Value.PurgeLegacyKeysOnPublish)
            await PurgeLegacyKeysAsync(domain, key, fullVersion, cancellationToken);

        return Result.Ok();
    }

    /// <inheritdoc />
    public async Task<Result> InvalidateAsync(string domain, string key, string version, CancellationToken cancellationToken = default)
    {
        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationRemove, CreateFullKey(domain, key, version), ComponentKeyName);

        // A full version identifies one revision, so its body is the only body worth dropping.
        if (InstanceDataVersionComparer.IsFullVersion(version))
            await TryRemoveAsync(CreateFullKey(domain, key, version), cancellationToken);

        // Deliberately not warmed: the version may have been deactivated or deleted, so the correct
        // winner is whatever the database says on the next read.
        var generation = await generationProvider.BumpAsync(ComponentKeyName, domain, key, cancellationToken);
        CacheActivityHelper.SetGeneration(activity, generation);

        if (options.Value.PurgeLegacyKeysOnPublish)
            await PurgeLegacyKeysAsync(domain, key, version, cancellationToken);

        return Result.Ok();
    }

    public void Dispose()
    {
    }

    // ────────────────────────────────────────────────────────────────────
    // Private: read paths
    // ────────────────────────────────────────────────────────────────────

    private async Task<Result<T>> GetResolvedAsync(
        string domain,
        string key,
        string? requested,
        CancellationToken cancellationToken)
    {
        var spelling = InstanceDataVersionComparer.NormalizeRequest(requested);
        var generation = await generationProvider.GetAsync(ComponentKeyName, domain, key, cancellationToken);
        var redisKey = CreateResolutionKey(domain, key, generation, spelling);

        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationGet, redisKey, ComponentKeyName);
        CacheActivityHelper.SetGeneration(activity, generation);

        var envelope = await TryGetEnvelopeAsync(redisKey, activity, cancellationToken);
        if (envelope is not null)
        {
            CacheActivityHelper.SetCacheHit(activity, true);

            if (envelope.IsNegative)
            {
                CacheActivityHelper.SetNegative(activity, true);
                return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, requested));
            }

            if (envelope.Entity is not null)
            {
                HydrateReference(envelope);
                return Result<T>.Ok(envelope.Entity);
            }
        }

        CacheActivityHelper.SetCacheHit(activity, false);

        return await ResolveCoalescedAsync(domain, key, requested, spelling, redisKey, activity, cancellationToken);
    }

    /// <summary>
    /// Shares one backend resolution between all callers currently missing the same resolution key.
    /// </summary>
    /// <remarks>
    /// The shared load runs on the first caller's cancellation token. If that caller disappears the
    /// waiters observe the cancellation and resolve again on their next request; the alternative — an
    /// unbounded token — would keep loading for callers that have already gone away. The window is
    /// narrow because, generations being what they are, misses only happen after a publish.
    /// </remarks>
    private async Task<Result<T>> ResolveCoalescedAsync(
        string domain,
        string key,
        string? requested,
        string spelling,
        string redisKey,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var resolution = _inFlightResolutions.GetOrAdd(
            redisKey,
            _ => new Lazy<Task<Result<T>>>(
                () => ResolveFromBackendAsync(domain, key, requested, spelling, redisKey, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        CacheActivityHelper.SetCoalesced(activity, resolution.IsValueCreated);

        try
        {
            return await resolution.Value;
        }
        finally
        {
            _inFlightResolutions.TryRemove(redisKey, out _);
        }
    }

    private async Task<Result<T>> ResolveFromBackendAsync(
        string domain,
        string key,
        string? requested,
        string spelling,
        string redisKey,
        CancellationToken cancellationToken)
    {
        var allResult = await backend.LoadAllByKeyAsync(domain, key, cancellationToken);
        if (!allResult.IsSuccess || allResult.Value!.Count == 0)
            return await CacheNegativeAsync(domain, key, requested, spelling, redisKey, cancellationToken);

        var allVersions = allResult.Value!;

        // The normalized spelling is what the answer gets filed under, so it has to be what the answer
        // was computed from — otherwise a padded or differently-cased request could cache one component
        // under the key another request reads. Normalization only trims and lowercases, and every
        // comparison in FindBestMatch that can see letters is already case-insensitive, so resolution
        // semantics are unchanged.
        var bestMatch = InstanceDataVersionComparer.FindBestMatch(
            allVersions.Select(e => e.Version), spelling);

        if (string.IsNullOrEmpty(bestMatch))
            return await CacheNegativeAsync(domain, key, requested, spelling, redisKey, cancellationToken);

        var matched = allVersions.FirstOrDefault(e =>
            string.Equals(e.Version, bestMatch, StringComparison.OrdinalIgnoreCase));

        if (matched is null)
            return await CacheNegativeAsync(domain, key, requested, spelling, redisKey, cancellationToken);

        var envelope = CreateEnvelope(matched);

        // Only the spelling that was asked for. Writing the other spellings here would be guessing at
        // resolutions nobody requested, and one of those guesses being wrong is how a resolution cache
        // goes stale in the first place.
        await TryWriteAsync(redisKey, envelope, ResolutionEntryOptions(), cancellationToken);

        // The body is immutable and was loaded anyway, so a pinned read of this exact version can reuse
        // it instead of going back to the database.
        await TryWriteAsync(
            CreateFullKey(domain, key, matched.Version),
            envelope,
            FullEntryOptions(),
            cancellationToken);

        logger.ComponentCacheResolvedFromBackend(ComponentKeyName, domain, key, spelling, matched.Version);

        return Result<T>.Ok(matched);
    }

    private async Task<Result<T>> CacheNegativeAsync(
        string domain,
        string key,
        string? requested,
        string spelling,
        string redisKey,
        CancellationToken cancellationToken)
    {
        // Short-lived, so a reference to a version that is about to be published starts working promptly
        // even without a generation bump; a bump clears it immediately.
        await TryWriteAsync(
            redisKey,
            new CacheEnvelope<T> { Domain = domain, Key = key, IsNegative = true },
            NegativeEntryOptions(),
            cancellationToken);

        logger.ComponentCacheNegativeStored(ComponentKeyName, domain, key, spelling);

        return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, requested));
    }

    private async Task<Result<T>> GetFullVersionAsync(string domain, string key, string fullVersion, CancellationToken cancellationToken)
    {
        // Build metadata (+packageName) does not participate in comparison, so "1.5.6-pkg.1.1.56" and
        // "1.5.6-pkg.1.1.56+core" are the same version and must share one entry.
        var redisKey = CreateFullKey(domain, key, fullVersion);

        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationGet, redisKey, ComponentKeyName);

        var envelope = await TryGetEnvelopeAsync(redisKey, activity, cancellationToken);
        if (envelope?.Entity is not null)
        {
            CacheActivityHelper.SetCacheHit(activity, true);
            HydrateReference(envelope);
            return Result<T>.Ok(envelope.Entity);
        }

        CacheActivityHelper.SetCacheHit(activity, false);

        var dbResult = await backend.LoadAsync(domain, key, fullVersion, cancellationToken);
        if (!dbResult.IsSuccess)
            return dbResult;

        var loaded = dbResult.Value!;

        await TryWriteAsync(redisKey, CreateEnvelope(loaded), FullEntryOptions(), cancellationToken);

        return Result<T>.Ok(loaded);
    }

    // ────────────────────────────────────────────────────────────────────
    // Private: write paths
    // ────────────────────────────────────────────────────────────────────

    private async Task WarmResolutionsAsync(
        string domain,
        string key,
        T entity,
        string generation,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        List<T> candidates;
        try
        {
            var allResult = await backend.LoadAllByKeyAsync(domain, key, cancellationToken);
            candidates = allResult.IsSuccess ? allResult.Value! : [];
        }
        catch (Exception ex)
        {
            // Warming is an optimization; reads will resolve on demand. Publishing must not fail for it.
            CacheActivityHelper.SetError(activity, ex);
            logger.ComponentCacheOperationFailed(ex, CacheActivityHelper.OperationWarmup, CreateFullKey(domain, key, entity.Version));
            return;
        }

        // The entity may not be visible to a fresh query yet, depending on where the publish sits
        // relative to its transaction. Adding it makes the resolution below correct either way — and
        // because the winner is re-resolved rather than assumed, it stays correct when the published
        // version is older than something already stored.
        if (!candidates.Any(e => string.Equals(e.Version, entity.Version, StringComparison.OrdinalIgnoreCase)))
            candidates = [.. candidates, entity];

        var versions = candidates.Select(e => e.Version).ToList();

        foreach (var spelling in DeriveCommonSpellings(entity.Version))
        {
            var bestMatch = InstanceDataVersionComparer.FindBestMatch(versions, spelling);
            if (string.IsNullOrEmpty(bestMatch))
                continue;

            var winner = candidates.FirstOrDefault(e =>
                string.Equals(e.Version, bestMatch, StringComparison.OrdinalIgnoreCase));
            if (winner is null)
                continue;

            await TryWriteAsync(
                CreateResolutionKey(domain, key, generation, spelling),
                CreateEnvelope(winner),
                ResolutionEntryOptions(),
                cancellationToken);
        }
    }

    /// <summary>
    /// The request spellings components are actually authored with: <c>latest</c>, the artifact version,
    /// MAJOR.MINOR and MAJOR.
    /// </summary>
    /// <remarks>
    /// Deliberately not a claim about which spellings a publish affects — that set is not enumerable,
    /// which is why resolutions are generation-scoped rather than individually invalidated. Used for
    /// warming (so a deploy does not hand the first request of each a database load) and for purging the
    /// pre-generation layout, whose keys were named after exactly these forms.
    /// </remarks>
    private static IEnumerable<string> DeriveCommonSpellings(string fullVersion)
    {
        var spellings = new List<string> { InstanceDataVersionComparer.LatestKeyword };

        var artifactVersion = InstanceDataVersionComparer.GetArtifactVersion(fullVersion);
        if (!string.IsNullOrEmpty(artifactVersion))
            spellings.Add(InstanceDataVersionComparer.NormalizeRequest(artifactVersion));

        if (InstanceDataVersionComparer.GetMajorMinor(fullVersion) is { Length: > 0 } majorMinor)
            spellings.Add(majorMinor);

        if (InstanceDataVersionComparer.GetMajor(fullVersion) is { Length: > 0 } major)
            spellings.Add(major);

        return spellings.Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// Deletes keys written by the pre-generation layout, which had no expiration and so would otherwise
    /// linger — and, during a rolling deployment, keep being served by pods running the old build.
    /// </summary>
    private async Task PurgeLegacyKeysAsync(string domain, string key, string version, CancellationToken cancellationToken)
    {
        await TryRemoveAsync(LegacyLatestKey(domain, key), cancellationToken);

        foreach (var spelling in DeriveCommonSpellings(version))
        {
            if (spelling == InstanceDataVersionComparer.LatestKeyword)
                continue;

            await TryRemoveAsync(LegacyArtifactKey(domain, key, spelling), cancellationToken);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Private: helpers
    // ────────────────────────────────────────────────────────────────────

    private async Task<CacheEnvelope<T>?> TryGetEnvelopeAsync(
        string redisKey,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        try
        {
            return await distributedCache.GetAsync<CacheEnvelope<T>>(redisKey, cancellationToken);
        }
        catch (Exception ex)
        {
            CacheActivityHelper.SetError(activity, ex);
            logger.ComponentCacheOperationFailed(ex, CacheActivityHelper.OperationGet, redisKey);
            return null;
        }
    }

    private async Task TryWriteAsync(
        string redisKey,
        CacheEnvelope<T> envelope,
        DistributedCacheEntryOptions entryOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            await distributedCache.SetAsync(redisKey, envelope, entryOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.ComponentCacheOperationFailed(ex, CacheActivityHelper.OperationSet, redisKey);
        }
    }

    private async Task TryRemoveAsync(string redisKey, CancellationToken cancellationToken)
    {
        try
        {
            await distributedCache.RemoveAsync(redisKey, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.ComponentCacheOperationFailed(ex, CacheActivityHelper.OperationRemove, redisKey);
        }
    }

    private static void HydrateReference(CacheEnvelope<T> envelope)
    {
        var entity = envelope.Entity!;

        if (!string.IsNullOrEmpty(entity.Domain) &&
            !string.IsNullOrEmpty(entity.Key) &&
            !string.IsNullOrEmpty(entity.Version))
            return;

        entity.SetReference(new Reference(
            envelope.Key,
            envelope.Domain,
            envelope.Flow,
            envelope.Version));
    }

    private static CacheEnvelope<T> CreateEnvelope(T entity)
    {
        return new CacheEnvelope<T>
        {
            Domain = entity.Domain,
            Key = entity.Key,
            Version = entity.Version,
            Flow = entity.ComponentKey,
            Entity = entity
        };
    }

    private DistributedCacheEntryOptions FullEntryOptions() => new()
    {
        AbsoluteExpiration = timeProvider.GetUtcNow().AddSeconds(options.Value.FullVersionTtlSeconds)
    };

    private DistributedCacheEntryOptions ResolutionEntryOptions() => new()
    {
        AbsoluteExpiration = timeProvider.GetUtcNow().AddSeconds(options.Value.ResolutionTtlSeconds)
    };

    private DistributedCacheEntryOptions NegativeEntryOptions() => new()
    {
        AbsoluteExpiration = timeProvider.GetUtcNow().AddSeconds(options.Value.NegativeTtlSeconds)
    };

    private static string CreateFullKey(string domain, string key, string fullVersion)
        => $"{ComponentKeyName}:{domain}:{key}:full:{InstanceDataVersionComparer.CanonicalFullVersion(fullVersion)}";

    private static string CreateResolutionKey(string domain, string key, string generation, string spelling)
        => $"{ComponentKeyName}:{domain}:{key}:res:{generation}:{spelling}";

    private static string LegacyLatestKey(string domain, string key)
        => $"{ComponentKeyName}:{domain}:{key}:latest";

    private static string LegacyArtifactKey(string domain, string key, string spelling)
        => $"{ComponentKeyName}:{domain}:{key}:artifact:{spelling}";
}
