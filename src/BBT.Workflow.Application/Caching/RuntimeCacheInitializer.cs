using BBT.Workflow.Definitions;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Caching;

/// <summary>
/// Implementation of cache initializer that loads all workflow components from the database
/// and initializes the domain cache context. This centralizes the cache initialization logic
/// that was previously scattered across multiple services.
/// </summary>
public sealed class RuntimeCacheInitializer(
    IServiceScopeFactory scopeFactory,
    IDomainCacheContext domainCacheContext) : IRuntimeCacheInitializer
{
    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        async Task<IEnumerable<T?>> LoadAsync<T>(CancellationToken ct)
            where T : class, IDomainEntity, IReferenceSetter
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var runtimeService = scope.ServiceProvider.GetRequiredService<IRuntimeService>();
            return await runtimeService.GetAsync<T>(ct);
        }

        // Load all entity types in parallel for better performance
        var flowsTask = LoadAsync<Definitions.Workflow>(cancellationToken);
        var tasksTask = LoadAsync<WorkflowTask>(cancellationToken);
        var functionsTask = LoadAsync<Function>(cancellationToken);
        var viewsTask = LoadAsync<View>(cancellationToken);
        var schemasTask = LoadAsync<SchemaDefinition>(cancellationToken);
        var extensionsTask = LoadAsync<Extension>(cancellationToken);

        // Wait for all tasks to complete
        await Task.WhenAll(flowsTask, tasksTask, functionsTask, viewsTask, schemasTask, extensionsTask);

        // Build the initial data dictionary
        var initialData = new Dictionary<Type, object>
        {
            { typeof(Definitions.Workflow), flowsTask.Result ?? [] },
            { typeof(WorkflowTask), tasksTask.Result ?? [] },
            { typeof(SchemaDefinition), schemasTask.Result ?? [] },
            { typeof(Function), functionsTask.Result ?? [] },
            { typeof(View), viewsTask.Result ?? [] },
            { typeof(Extension), extensionsTask.Result ?? [] }
        };

        // Initialize the domain cache context
        await domainCacheContext.InitializeAsync(initialData, cancellationToken);
    }
}