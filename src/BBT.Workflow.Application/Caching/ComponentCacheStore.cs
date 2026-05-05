using BBT.Aether.Domain.Entities;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Caching;

/// <summary>
/// Implementation of <see cref="IComponentCacheStore"/> that provides caching operations for workflow components.
/// Uses domain-specific cache context to store and retrieve workflows, tasks, schemas, functions, views, and extensions.
/// </summary>
/// <param name="cacheContext">The domain cache context used for caching operations.</param>
public sealed class ComponentCacheStore(
    IDomainCacheContext cacheContext) : IComponentCacheStore
{
    /// <inheritdoc />
    public async Task<Result<Definitions.Workflow>> GetFlowAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default)
    {
        return await cacheContext.Workflows.GetByVersionAsync(domain, key, version, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<WorkflowTask>> GetTaskAsync(string domain, string key, string? version,
        CancellationToken cancellationToken = default)
    {
        return await cacheContext.Tasks.GetByVersionAsync(domain, key, version, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<SchemaDefinition>> GetSchemaAsync(string domain, string key, string? version,
        CancellationToken cancellationToken = default)
    {
        return await cacheContext.Schemas.GetByVersionAsync(domain, key, version, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<Function>> GetFunctionAsync(string domain, string key, string? version,
        CancellationToken cancellationToken = default)
    {
        return await cacheContext.Functions.GetByVersionAsync(domain, key, version, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<View>> GetViewAsync(string domain, string key, string? version,
        CancellationToken cancellationToken = default)
    {
        return await cacheContext.Views.GetByVersionAsync(domain, key, version, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<Extension>> GetExtensionAsync(string domain, string key, string? version,
        CancellationToken cancellationToken = default)
    {
        return await cacheContext.Extensions.GetByVersionAsync(domain, key, version, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<IEnumerable<Extension>>> GetAllExtensionsAsync(
        string domain,
        CancellationToken cancellationToken = default)
    {
        // For bulk extension retrieval, we get latest for the domain.
        // This returns a single latest extension per key; for multi-key domains,
        // the caller should enumerate keys separately.
        var result = await cacheContext.Extensions.GetLatestByNameAsync(domain, string.Empty, cancellationToken);

        if (!result.IsSuccess)
            return Result<IEnumerable<Extension>>.Ok(Enumerable.Empty<Extension>());

        return Result<IEnumerable<Extension>>.Ok(new[] { result.Value! });
    }

    /// <inheritdoc />
    public async Task<Result> SetAsync<T>(T entity, CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter
    {
        var cacheSet = cacheContext.Set<T>();
        return await cacheSet.SetAsync(entity, cancellationToken);
    }
}
