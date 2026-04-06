using BBT.Aether.DependencyInjection;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.DefinitionContext;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for <see cref="IServiceScopeFactory"/> to simplify scope creation and management.
/// Provides automatic AmbientServiceProvider restoration and scope disposal in finally blocks.
/// </summary>
public static class ServiceScopeFactoryExtensions
{
    /// <summary>
    /// Executes an async operation in a new DI scope with automatic AmbientServiceProvider restoration.
    /// The scope and ambient provider are guaranteed to be restored even if the operation throws.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="scopeFactory">The service scope factory.</param>
    /// <param name="action">The async operation to execute with the scoped service provider.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A Result containing the operation outcome.</returns>
    /// <remarks>
    /// This method:
    /// 1. Saves the current AmbientServiceProvider.Current
    /// 2. Creates a new async scope
    /// 3. Sets AmbientServiceProvider.Current to the new scope's service provider
    /// 4. Executes the provided action
    /// 5. Restores the previous AmbientServiceProvider.Current in finally block
    /// 6. Disposes the scope in finally block
    /// 
    /// Use this when you need isolated DI scope with proper ambient context management,
    /// similar to how TransitionRunner manages execution scopes.
    /// </remarks>
    /// <example>
    /// <code>
    /// return await scopeFactory.ExecuteInScopeAsync(async (sp, ct) =>
    /// {
    ///     var service = sp.GetRequiredService&lt;IMyService&gt;();
    ///     return await service.DoWorkAsync(ct);
    /// }, cancellationToken);
    /// </code>
    /// </example>
    public static async Task<Result<T>> ExecuteInScopeAsync<T>(
        this IServiceScopeFactory scopeFactory,
        Func<IServiceProvider, CancellationToken, Task<Result<T>>> action,
        CancellationToken cancellationToken = default)
    {
        var previousAmbient = AmbientServiceProvider.Current;
        var scope = scopeFactory.CreateAsyncScope();
        try
        {
            var sp = scope.ServiceProvider;
            AmbientServiceProvider.Current = sp;
            return await action(sp, cancellationToken);
        }
        finally
        {
            AmbientServiceProvider.Current = previousAmbient;
            await scope.DisposeAsync();
        }
    }

    /// <summary>
    /// Executes an async operation in a new DI scope with automatic AmbientServiceProvider restoration.
    /// Non-generic version for operations that return Result without a value.
    /// </summary>
    /// <param name="scopeFactory">The service scope factory.</param>
    /// <param name="action">The async operation to execute with the scoped service provider.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A Result indicating success or failure.</returns>
    /// <remarks>
    /// This is the non-generic overload for operations that don't return a value,
    /// only success/failure status. See the generic version for detailed documentation.
    /// </remarks>
    /// <example>
    /// <code>
    /// return await scopeFactory.ExecuteInScopeAsync(async (sp, ct) =>
    /// {
    ///     var service = sp.GetRequiredService&lt;IMyService&gt;();
    ///     await service.ProcessAsync(ct);
    ///     return Result.Ok();
    /// }, cancellationToken);
    /// </code>
    /// </example>
    public static async Task<Result> ExecuteInScopeAsync(
        this IServiceScopeFactory scopeFactory,
        Func<IServiceProvider, CancellationToken, Task<Result>> action,
        CancellationToken cancellationToken = default)
    {
        var previousAmbient = AmbientServiceProvider.Current;
        var scope = scopeFactory.CreateAsyncScope();
        try
        {
            var sp = scope.ServiceProvider;
            AmbientServiceProvider.Current = sp;
            return await action(sp, cancellationToken);
        }
        finally
        {
            AmbientServiceProvider.Current = previousAmbient;
            await scope.DisposeAsync();
        }
    }

    /// <summary>
    /// Executes an async workflow operation in a new DI scope with automatic workflow loading and context management.
    /// Combines scope management, workflow loading from cache, and IWorkflowContext setup in a single call.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="scopeFactory">The service scope factory.</param>
    /// <param name="action">The async operation to execute with the scoped service provider.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A Result containing the operation outcome or workflow loading error.</returns>
    /// <remarks>
    /// This method automates the common pattern of:
    /// 1. Creating a new DI scope with AmbientServiceProvider restoration
    /// 2. Loading workflow from IComponentCacheStore
    /// 3. Setting workflow in IWorkflowContext for downstream services
    /// 4. Executing business logic
    /// 
    /// The loaded workflow is set in IWorkflowContext, so downstream services can access it via DI.
    /// If workflow loading fails, the error is returned immediately without executing the action.
    /// </remarks>
    /// <example>
    /// <code>
    /// return await scopeFactory.ExecuteWithWorkflowAsync(
    ///     domain,
    ///     workflowKey,
    ///     workflowVersion,
    ///     async (sp, ct) =>
    ///     {
    ///         var core = sp.GetRequiredService&lt;IWorkflowExecutionCore&gt;();
    ///         return await core.ExecuteAsync(ct);
    ///     },
    ///     cancellationToken);
    /// </code>
    /// </example>
    /// <summary>
    /// Executes an async operation in a new DI scope with automatic AmbientServiceProvider restoration.
    /// Raw version for operations that return any type (not necessarily Result&lt;T&gt;).
    /// Use this for ConditionalResult&lt;T&gt;, PostCommitResult, or other non-Result return types.
    /// </summary>
    public static async Task<T> ExecuteInScopeRawAsync<T>(
        this IServiceScopeFactory scopeFactory,
        Func<IServiceProvider, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        var previousAmbient = AmbientServiceProvider.Current;
        var scope = scopeFactory.CreateAsyncScope();
        try
        {
            var sp = scope.ServiceProvider;
            AmbientServiceProvider.Current = sp;
            return await action(sp, cancellationToken);
        }
        finally
        {
            AmbientServiceProvider.Current = previousAmbient;
            await scope.DisposeAsync();
        }
    }

    public static Task<Result<T>> ExecuteWithWorkflowAsync<T>(
        this IServiceScopeFactory scopeFactory,
        string domain,
        string workflowKey,
        string? workflowVersion,
        Func<IServiceProvider, CancellationToken, Task<Result<T>>> action,
        CancellationToken cancellationToken = default)
    {
        return scopeFactory.ExecuteInScopeAsync(async (sp, ct) =>
        {
            var componentCacheStore = sp.GetRequiredService<IComponentCacheStore>();
            var workflowContext = sp.GetRequiredService<IWorkflowContext>();

            // Load workflow from cache
            var workflowResult = await componentCacheStore.GetFlowAsync(
                domain,
                workflowKey,
                workflowVersion,
                ct);

            if (!workflowResult.IsSuccess)
                return Result<T>.Fail(workflowResult.Error);

            var workflow = workflowResult.Value!;

            // Set workflow in scoped context for downstream use
            workflowContext.SetWorkflow(workflow);

            // Execute action - workflow is available via IWorkflowContext
            return await action(sp, ct);
        }, cancellationToken);
    }
}
