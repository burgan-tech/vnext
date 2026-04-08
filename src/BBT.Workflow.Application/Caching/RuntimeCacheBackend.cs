using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Caching;

/// <summary>
/// Implementation of ICacheBackend that uses IRuntimeService to load entities from the database.
/// This class provides the bridge between the cache layer and the runtime data access layer.
/// Infrastructure errors (database, connection) are allowed to throw exceptions per Railway Pattern guidelines.
/// </summary>
/// <typeparam name="T">The type of entity to load from the runtime backend</typeparam>
public sealed class RuntimeCacheBackend<T>(
    IServiceScopeFactory scopeFactory)
    : ICacheBackend<T>
    where T : class, IDomainEntity, IReferenceSetter
{
    /// <summary>
    /// Loads an entity from the backend with smart version matching.
    /// </summary>
    /// <param name="domain">The domain identifier</param>
    /// <param name="key">The entity key/name</param>
    /// <param name="version">The version to search for. Supports:
    /// <list type="bullet">
    ///     <item><description>null/empty: Returns the latest version</description></item>
    ///     <item><description>Full version (e.g., "1.0.0-pkg.1.17.0+account"): Exact match</description></item>
    ///     <item><description>Artifact version (e.g., "1.0.0" or "1.0.0-alpha.1"): Returns highest pkg version for that artifact</description></item>
    ///     <item><description>Partial version (e.g., "1.0"): Returns highest version matching the prefix</description></item>
    /// </list>
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>A Result containing the matched entity or an error</returns>
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
            
            if (entity is null)
            {
                return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, version));
            }
            
            return Result<T>.Ok(entity);
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
}

