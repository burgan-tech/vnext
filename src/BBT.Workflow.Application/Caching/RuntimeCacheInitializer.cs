using BBT.Workflow.Definitions;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Caching;

/// <summary>
/// Implementation of cache initializer that loads all workflow components from the database
/// and initializes the domain cache context. This centralizes the cache initialization logic
/// that was previously scattered across multiple services.
/// </summary>
public sealed class RuntimeCacheInitializer(
    IServiceScopeFactory scopeFactory,
    IDomainCacheContext domainCacheContext,
    CacheInitializationGate gate,
    ILogger<RuntimeCacheInitializer> logger) : IRuntimeCacheInitializer
{
    /// <inheritdoc />
    public async Task InitializeAsync(bool fullLoad = false, CancellationToken cancellationToken = default)
    {
        if (!gate.TryAcquire())
        {
            logger.LogWarning(
                "Cache initialization already in progress on this pod. Skipping concurrent request. (fullLoad={FullLoad})",
                fullLoad);
            return;
        }

        try
        {
            var since = ResolveLoadSince(fullLoad);
            var capturedAt = DateTime.UtcNow;
            var data = await LoadAllEntitiesFromDatabaseAsync(since, cancellationToken);

            if (since.HasValue)
                await domainCacheContext.MergeAsync(data, cancellationToken);
            else
                await domainCacheContext.InitializeAsync(data, cancellationToken);

            gate.SetLastInitializedAt(capturedAt);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task InitializeWithDistributedCacheAsync(bool fullLoad = false, CancellationToken cancellationToken = default)
    {
        if (!gate.TryAcquire())
        {
            logger.LogWarning(
                "Cache initialization already in progress on this pod. Skipping concurrent request. (fullLoad={FullLoad})",
                fullLoad);
            return;
        }

        try
        {
            var since = ResolveLoadSince(fullLoad);
            var capturedAt = DateTime.UtcNow;
            var data = await LoadAllEntitiesFromDatabaseAsync(since, cancellationToken);

            if (since.HasValue)
                await domainCacheContext.MergeAsync(data, cancellationToken);
            else
                await domainCacheContext.InitializeWithDistributedCacheAsync(data, cancellationToken);

            gate.SetLastInitializedAt(capturedAt);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Returns the <c>since</c> timestamp to use for this load:
    /// <c>null</c> → full load (replace all); non-null → incremental (merge delta).
    /// </summary>
    private DateTime? ResolveLoadSince(bool fullLoad)
    {
        if (fullLoad)
            return null;

        return gate.LastInitializedAt;
    }

    private async Task<Dictionary<Type, object>> LoadAllEntitiesFromDatabaseAsync(
        DateTime? since,
        CancellationToken cancellationToken)
    {
        // Load entity types sequentially to avoid concurrent DB connection pressure.
        var flows = await LoadAsync<Definitions.Workflow>(since, cancellationToken);
        var tasks = await LoadAsync<WorkflowTask>(since, cancellationToken);
        var functions = await LoadAsync<Function>(since, cancellationToken);
        var views = await LoadAsync<View>(since, cancellationToken);
        var schemas = await LoadAsync<SchemaDefinition>(since, cancellationToken);
        var extensions = await LoadAsync<Extension>(since, cancellationToken);

        return new Dictionary<Type, object>
        {
            { typeof(Definitions.Workflow), flows },
            { typeof(WorkflowTask), tasks },
            { typeof(SchemaDefinition), schemas },
            { typeof(Function), functions },
            { typeof(View), views },
            { typeof(Extension), extensions }
        };
    }

    private async Task<IEnumerable<T?>> LoadAsync<T>(DateTime? since, CancellationToken ct)
        where T : class, IDomainEntity, IReferenceSetter
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var runtimeService = scope.ServiceProvider.GetRequiredService<IRuntimeService>();
        return await runtimeService.GetAsync<T>(since, ct);
    }
}
