using BBT.Workflow.Execution.Strategies;
using BBT.Workflow.Instances;
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
    ITransitionRunner transitionRunner) : IWorkflowExecutionService, IWorkflowExecutionCore
{
    /// <inheritdoc />
    /// <summary>
    /// Executes a workflow transition by delegating to TransitionRunner.
    /// The runner manages UoW lifecycle and inline auto chain processing.
    /// </summary>
    public Task<Result<TransitionOutput>> ExecuteTransitionAsync(
        WorkflowExecutionContext context,
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
            .BindAsync(execCtx => BuildCoreOutputAsync(execCtx));
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
    /// Builds the core output including transition output and deferred domain events.
    /// Uses the pipeline's in-memory instance state (already committed via autoSave)
    /// to avoid a redundant DB round-trip within the same UoW.
    /// </summary>
    private static Task<Result<TransitionCoreOutput>> BuildCoreOutputAsync(
        TransitionExecutionContext executionContext)
    {
        var outputResult = BuildTransitionOutput(executionContext);
        if (!outputResult.IsSuccess)
            return Task.FromResult(Result<TransitionCoreOutput>.Fail(outputResult.Error));

        // Snapshot continuation work as a pure read BEFORE consuming events.
        // Behavior-preserving: nothing acts on this snapshot yet (see S3/S4).
        var continuations = executionContext.Directives.ToContinuations();
        var deferredEvents = executionContext.Directives.ConsumeDeferredEvents();
        return Task.FromResult(
            Result<TransitionCoreOutput>.Ok(new TransitionCoreOutput(
                outputResult.Value!,
                deferredEvents,
                continuations,
                executionContext)));
    }

    /// <summary>
    /// Builds the transition output from the execution context's in-memory instance state.
    /// Pipeline steps have already persisted all mutations via autoSave, so the context
    /// holds the authoritative status — no additional DB read is needed.
    /// </summary>
    private static Result<TransitionOutput> BuildTransitionOutput(
        TransitionExecutionContext executionContext)
    {
        if (executionContext.ClientResponse is not null)
        {
            if (executionContext.ClientResponse.Error is { } responseError)
                return Result<TransitionOutput>.Fail(responseError);

            return Result<TransitionOutput>.Ok(new TransitionOutput
            {
                Id = executionContext.InstanceId,
                Status = executionContext.ClientResponse.Status
            });
        }

        return Result<TransitionOutput>.Ok(new TransitionOutput
        {
            Id = executionContext.InstanceId,
            Status = executionContext.Instance.Status,
            PipelineInstance = executionContext.Instance
        });
    }
}
