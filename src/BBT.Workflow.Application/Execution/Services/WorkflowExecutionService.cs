using BBT.Workflow.Execution.Strategies;
using BBT.Workflow.Instances;
using BBT.Aether.Aspects;
using BBT.Aether.Results;

namespace BBT.Workflow.Execution.Services;

/// <inheritdoc cref="IWorkflowExecutionService" />
/// <inheritdoc cref="IWorkflowExecutionCore" />
/// <summary>
/// Orchestrates workflow transition execution.
/// Acts as a facade delegating to TransitionRunner for chained execution,
/// and implements IWorkflowExecutionCore for single transition core logic.
/// </summary>
public sealed class WorkflowExecutionService(
    IExecutionStrategyFactory execFactory,
    IInstanceRepository instanceRepository,
    ITransitionRunner transitionRunner) : IWorkflowExecutionService, IWorkflowExecutionCore
{
    /// <inheritdoc />
    /// <summary>
    /// Executes a workflow transition by delegating to TransitionRunner.
    /// The runner manages UoW lifecycle and inline auto chain processing.
    /// </summary>
    [Log]
    [Trace]
    public Task<Result<TransitionOutput>> ExecuteTransitionAsync(
        [Enrich] WorkflowExecutionContext context,
        CancellationToken cancellationToken = default)
        => transitionRunner.RunAsync(context, cancellationToken);

    /// <inheritdoc />
    /// <summary>
    /// Core transition execution without UoW management.
    /// The caller (TransitionRunner) is responsible for UoW lifecycle.
    /// Executes the transition pipeline and returns output with directives snapshot.
    /// </summary>
    /// <remarks>
    /// IMPORTANT: This method does NOT have [UnitOfWork] attribute.
    /// UoW is managed by TransitionRunner to ensure proper post-commit processing.
    /// </remarks>
    public Task<Result<TransitionCoreOutput>> ExecuteTransitionCoreAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        return GetExecutionStrategy(context.Mode)
            .BindAsync(strategy => ExecuteStrategyAsync(strategy, context, cancellationToken))
            .BindAsync(execCtx => BuildCoreOutputAsync(context, execCtx, cancellationToken));
    }

    /// <summary>
    /// Gets the execution strategy for the specified mode.
    /// </summary>
    private Result<ITransitionStrategy> GetExecutionStrategy(ExecMode mode)
        => execFactory.Get(mode);

    /// <summary>
    /// Executes the strategy with the given context.
    /// Separated for clarity and testability.
    /// </summary>
    private static Task<Result<TransitionExecutionContext>> ExecuteStrategyAsync(
        ITransitionStrategy strategy,
        WorkflowExecutionContext context,
        CancellationToken cancellationToken)
        => strategy.ExecuteAsync(context, cancellationToken);

    /// <summary>
    /// Builds the core output including transition output and directives snapshot.
    /// The snapshot captures inline auto queue for post-commit processing.
    /// </summary>
    private async Task<Result<TransitionCoreOutput>> BuildCoreOutputAsync(
        WorkflowExecutionContext context,
        TransitionExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        // Build transition output
        var outputResult = await BuildTransitionOutputAsync(context, executionContext, cancellationToken);
        if (!outputResult.IsSuccess)
        {
            return Result<TransitionCoreOutput>.Fail(outputResult.Error);
        }
        
        return Result<TransitionCoreOutput>.Ok(new TransitionCoreOutput(outputResult.Value!));
    }

    /// <summary>
    /// Builds the transition output response.
    /// Uses Railway Programming with Map for type transformation.
    /// </summary>
    private async Task<Result<TransitionOutput>> BuildTransitionOutputAsync(
        WorkflowExecutionContext context,
        TransitionExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        // Early return if client response is available (no DB lookup needed)
        if (executionContext.ClientResponse is not null)
        {
            return Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Id = executionContext.InstanceId,
                Status = executionContext.ClientResponse.Status
            });
        }

        // Fetch fresh instance and transform to output using Map
        return await FetchInstanceAsync(context.InstanceId, cancellationToken)
            .MapAsync(MapInstanceToOutput);
    }

    /// <summary>
    /// Fetches the workflow instance from repository.
    /// Uses ToResult extension for null-safe Result conversion.
    /// </summary>
    private async Task<Result<Instance>> FetchInstanceAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(instanceId, cancellationToken);
        return instance.ToResult(
            ExecutionErrors.InstanceNotFoundForResponse(instanceId));
    }

    /// <summary>
    /// Maps a workflow instance to TransitionOutput.
    /// Pure transformation function - no side effects.
    /// </summary>
    private static TransitionOutput MapInstanceToOutput(Instance instance)
        => new()
        {
            Id = instance.Id,
            Status = instance.Status
        };
}
