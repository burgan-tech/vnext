using BBT.Aether.DependencyInjection;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.DefinitionContext;
using BBT.Workflow.Instances;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for <see cref="IServiceScopeFactory"/> to simplify scope creation and management.
/// Provides automatic AmbientServiceProvider restoration and scope disposal in finally blocks.
/// </summary>
public static class ServiceScopeFactoryExtensions
{
    #region ExecuteInScopeAsync

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
    /// Non-generic version for operations that return <see cref="Result"/> without a value.
    /// </summary>
    /// <param name="scopeFactory">The service scope factory.</param>
    /// <param name="action">The async operation to execute with the scoped service provider.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A Result indicating success or failure.</returns>
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
    /// Executes an async operation in a new DI scope with automatic AmbientServiceProvider restoration.
    /// Raw version for operations that return any type — use for <see cref="ConditionalResult{T}"/>,
    /// custom result wrappers, or other non-<see cref="Result{T}"/> return types.
    /// </summary>
    /// <typeparam name="T">The raw return type of the action.</typeparam>
    /// <param name="scopeFactory">The service scope factory.</param>
    /// <param name="action">The async operation to execute with the scoped service provider.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The raw value returned by the action.</returns>
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

    #endregion

    #region ExecuteWithWorkflowAsync

    /// <summary>
    /// Executes an async workflow operation in a new DI scope with automatic workflow loading and context management.
    /// Returns <see cref="Result{T}"/>.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="scopeFactory">The service scope factory.</param>
    /// <param name="domain">The workflow domain used for cache lookup.</param>
    /// <param name="workflowKey">The workflow key used for schema activation and cache lookup.</param>
    /// <param name="workflowVersion">Optional workflow version; null resolves to the latest version.</param>
    /// <param name="action">The async operation to execute once the workflow is loaded.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A <see cref="Result{T}"/> containing the operation outcome or a workflow loading error.</returns>
    public static Task<Result<T>> ExecuteWithWorkflowAsync<T>(
        this IServiceScopeFactory scopeFactory,
        string domain,
        string workflowKey,
        string? workflowVersion,
        Func<IServiceProvider, CancellationToken, Task<Result<T>>> action,
        CancellationToken cancellationToken = default)
    {
        return scopeFactory.ExecuteInScopeAsync(
            (sp, ct) => WithWorkflowScopeAsync(
                sp, domain, workflowKey, workflowVersion, ct,
                () => action(sp, ct),
                Result<T>.Fail),
            cancellationToken);
    }

    /// <summary>
    /// Executes an async workflow operation in a new DI scope with automatic workflow loading and context management.
    /// Returns non-generic <see cref="Result"/>.
    /// </summary>
    /// <param name="scopeFactory">The service scope factory.</param>
    /// <param name="domain">The workflow domain used for cache lookup.</param>
    /// <param name="workflowKey">The workflow key used for schema activation and cache lookup.</param>
    /// <param name="workflowVersion">Optional workflow version; null resolves to the latest version.</param>
    /// <param name="action">The async operation to execute once the workflow is loaded.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A <see cref="Result"/> indicating success or failure.</returns>
    public static Task<Result> ExecuteWithWorkflowAsync(
        this IServiceScopeFactory scopeFactory,
        string domain,
        string workflowKey,
        string? workflowVersion,
        Func<IServiceProvider, CancellationToken, Task<Result>> action,
        CancellationToken cancellationToken = default)
    {
        return scopeFactory.ExecuteInScopeAsync(
            (sp, ct) => WithWorkflowScopeAsync(
                sp, domain, workflowKey, workflowVersion, ct,
                () => action(sp, ct),
                Result.Fail),
            cancellationToken);
    }

    /// <summary>
    /// Executes an async workflow operation in a new DI scope with automatic workflow loading and context management.
    /// Returns a plain <see cref="Task"/> — use when no result value is needed.
    /// Workflow loading errors are swallowed; the action is simply not executed.
    /// </summary>
    /// <param name="scopeFactory">The service scope factory.</param>
    /// <param name="domain">The workflow domain used for cache lookup.</param>
    /// <param name="workflowKey">The workflow key used for schema activation and cache lookup.</param>
    /// <param name="workflowVersion">Optional workflow version; null resolves to the latest version.</param>
    /// <param name="action">The async operation to execute once the workflow is loaded.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A <see cref="Task"/> that completes when the action finishes.</returns>
    public static Task ExecuteWithWorkflowAsync(
        this IServiceScopeFactory scopeFactory,
        string domain,
        string workflowKey,
        string? workflowVersion,
        Func<IServiceProvider, CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        return scopeFactory.ExecuteInScopeAsync(
            (sp, ct) => WithWorkflowScopeAsync(
                sp, domain, workflowKey, workflowVersion, ct,
                async () => { await action(sp, ct); return Result.Ok(); },
                Result.Fail),
            cancellationToken);
    }

    /// <summary>
    /// Executes an async workflow operation in a new DI scope with automatic workflow loading and context management.
    /// Returns <see cref="ConditionalResult{T}"/> — use for conditional HTTP responses such as 304 Not Modified.
    /// </summary>
    /// <typeparam name="T">The type of the value returned on success.</typeparam>
    /// <param name="scopeFactory">The service scope factory.</param>
    /// <param name="domain">The workflow domain used for cache lookup.</param>
    /// <param name="workflowKey">The workflow key used for schema activation and cache lookup.</param>
    /// <param name="workflowVersion">Optional workflow version; null resolves to the latest version.</param>
    /// <param name="action">The async operation to execute once the workflow is loaded.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A <see cref="ConditionalResult{T}"/> containing the outcome or a workflow loading error.</returns>
    public static Task<ConditionalResult<T>> ExecuteWithWorkflowAsync<T>(
        this IServiceScopeFactory scopeFactory,
        string domain,
        string workflowKey,
        string? workflowVersion,
        Func<IServiceProvider, CancellationToken, Task<ConditionalResult<T>>> action,
        CancellationToken cancellationToken = default)
    {
        return scopeFactory.ExecuteInScopeRawAsync(
            (sp, ct) => WithWorkflowScopeAsync(
                sp, domain, workflowKey, workflowVersion, ct,
                () => action(sp, ct),
                error => ConditionalResult<T>.Fail(error)),
            cancellationToken);
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Core workflow setup: activates the schema, loads the workflow from cache, sets it in
    /// <see cref="IWorkflowContext"/>, then delegates to <paramref name="action"/>.
    /// </summary>
    /// <typeparam name="TResult">The return type of the action.</typeparam>
    /// <param name="sp">The scoped service provider.</param>
    /// <param name="domain">The workflow domain used for cache lookup.</param>
    /// <param name="workflowKey">The workflow key used for schema activation and cache lookup.</param>
    /// <param name="workflowVersion">Optional workflow version.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="action">The operation to execute after the workflow is loaded.</param>
    /// <param name="onWorkflowLoadFailed">Called when the workflow cannot be loaded; maps the error to TResult.</param>
    private static async Task<TResult> WithWorkflowScopeAsync<TResult>(
        IServiceProvider sp,
        string domain,
        string workflowKey,
        string? workflowVersion,
        CancellationToken ct,
        Func<Task<TResult>> action,
        Func<Error, TResult> onWorkflowLoadFailed)
    {
        var currentSchema = sp.GetRequiredService<ICurrentSchema>();
        var componentCacheStore = sp.GetRequiredService<IComponentCacheStore>();
        var workflowContext = sp.GetRequiredService<IWorkflowContext>();

        using (currentSchema.Use(workflowKey))
        {
            var workflowResult = await componentCacheStore.GetFlowAsync(domain, workflowKey, workflowVersion, ct);

            if (!workflowResult.IsSuccess)
                return onWorkflowLoadFailed(workflowResult.Error);

            workflowContext.SetWorkflow(workflowResult.Value!);

            return await action();
        }
    }

    #endregion
}
