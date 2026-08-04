using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Caching;

/// <summary>
/// Implementation of ICacheBackend that uses IRuntimeService to load entities from the database.
/// This class provides the bridge between the cache layer and the runtime data access layer.
/// Infrastructure errors (database, connection) are allowed to throw exceptions per Railway Pattern guidelines.
/// </summary>
/// <typeparam name="T">The type of entity to load from the runtime backend</typeparam>
public sealed class RuntimeCacheBackend<T>(
    IServiceScopeFactory scopeFactory,
    ILogger<RuntimeCacheBackend<T>> logger)
    : ICacheBackend<T>
    where T : class, IDomainEntity, IReferenceSetter
{
    /// <summary>
    /// Loads every active version of a component key in the given domain.
    /// </summary>
    /// <param name="domain">The domain identifier</param>
    /// <param name="key">The entity key/name</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>A Result containing all active versions, which may be empty</returns>
    public async Task<Result<List<T>>> LoadAllByKeyAsync(
        string domain,
        string key,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var runtimeService = scope.ServiceProvider.GetRequiredService<IRuntimeService>();
        var runtimeInfoProvider = scope.ServiceProvider.GetRequiredService<IRuntimeInfoProvider>();

        runtimeInfoProvider.Check(domain);

        var all = await runtimeService.GetAsync<T>(key, cancellationToken);
        var filtered = all
            .Where(e => e is not null &&
                        string.Equals(e!.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .Select(e => e!)
            .ToList();

        return Result<List<T>>.Ok(filtered);
    }

    public async Task<Result<T>> LoadAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var runtimeService = scope.ServiceProvider.GetRequiredService<IRuntimeService>();
        var runtimeInfoProvider = scope.ServiceProvider.GetRequiredService<IRuntimeInfoProvider>();

        runtimeInfoProvider.Check(domain);

        // Infrastructure exceptions (DB, connection) will bubble up - this is expected per Railway Pattern
        
        // Full version → try exact match first via repository
        if (InstanceDataVersionComparer.IsFullVersion(version))
        {
            var entity = await runtimeService.GetAsync<T>(key, version!, cancellationToken);

            if (entity is not null)
            {
                return Result<T>.Ok(entity);
            }

            // Build metadata (+packageName) does not participate in comparison, so a request that omits
            // it names the same version as a stored value that carries it. The exact single-row lookup
            // above covers the common case where the caller echoes a stored version verbatim; only when
            // that misses is it worth loading the version list to compare canonical identities.
            return await LoadByCanonicalFullVersionAsync(runtimeService, domain, key, version!, cancellationToken);
        }

        // For null/empty, artifact, or partial version → key-filtered load + smart matching
        var all = await runtimeService.GetAsync<T>(key, cancellationToken);
        var filtered = all
            .Where(e => e is not null &&
                        string.Equals(e.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filtered.Count == 0)
        {
            return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, version));
        }

        // Use smart version matching
        var versions = filtered.Select(e => e!.Version).ToList();
        var bestMatchVersion = InstanceDataVersionComparer.FindBestMatch(versions, version);

        if (string.IsNullOrEmpty(bestMatchVersion))
        {
            return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, version));
        }

        var matched = filtered.FirstOrDefault(e => 
            string.Equals(e!.Version, bestMatchVersion, StringComparison.OrdinalIgnoreCase));

        if (matched is null)
        {
            return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, version));
        }

        return Result<T>.Ok(matched);
    }

    /// <summary>
    /// Resolves a full-version request by canonical identity, ignoring build metadata.
    /// </summary>
    /// <remarks>
    /// Two stored versions can share an artifact and package version while differing in build metadata
    /// (for example <c>+core</c> and <c>+customer</c>). Version comparison cannot separate them — they
    /// compare equal — so one is picked by a stable rule and the collision is logged rather than left to
    /// vary between calls.
    /// </remarks>
    private async Task<Result<T>> LoadByCanonicalFullVersionAsync(
        IRuntimeService runtimeService,
        string domain,
        string key,
        string version,
        CancellationToken cancellationToken)
    {
        var canonicalVersion = InstanceDataVersionComparer.CanonicalFullVersion(version);

        var all = await runtimeService.GetAsync<T>(key, cancellationToken);
        var matches = all
            .Where(e => e is not null &&
                        string.Equals(e.Domain, domain, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            InstanceDataVersionComparer.CanonicalFullVersion(e.Version),
                            canonicalVersion,
                            StringComparison.OrdinalIgnoreCase))
            .Select(e => e!)
            .OrderBy(e => e.Version, StringComparer.Ordinal)
            .ToList();

        if (matches.Count == 0)
        {
            return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, version));
        }

        var resolved = matches[0];

        if (matches.Count > 1)
        {
            logger.ComponentCacheBuildMetadataAmbiguity(
                T.ComponentTypeKey, domain, key, matches.Count, canonicalVersion, resolved.Version);
        }

        return Result<T>.Ok(resolved);
    }
}

