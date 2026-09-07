using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Execution.Continuations;

/// <summary>
/// Realizes the auto-chain continuation in-process: builds the next
/// <see cref="WorkflowExecutionContext"/> so the pipeline loop runs the chained
/// transition inside the same uninterrupted pipeline and UoW. The distributed status lock has
/// already been released after admission; it is not held across the chain.
/// </summary>
public sealed class InlineContinuationStrategy : IContinuationStrategy
{
    /// <inheritdoc />
    public ContinuationMode Mode => ContinuationMode.Inline;

    /// <inheritdoc />
    public Task<Result<WorkflowExecutionContext?>> DispatchAsync(
        TransitionExecutionContext current,
        CancellationToken cancellationToken)
    {
        var next = current.Directives.ConsumeNextTransition();
        if (next is null)
            return Task.FromResult(Result<WorkflowExecutionContext?>.Ok(null));

        var workflowContext = CreateNextWorkflowContext(current, next);
        return Task.FromResult(Result<WorkflowExecutionContext?>.Ok(workflowContext));
    }

    /// <summary>
    /// Creates a new <see cref="WorkflowExecutionContext"/> for the next transition in the chain.
    /// Identity/execution carrier only — the pipeline builds and validates a new execution context,
    /// reusing the current tracked instance/workflow only while it remains in the same UoW.
    /// </summary>
    private static WorkflowExecutionContext CreateNextWorkflowContext(
        TransitionExecutionContext currentContext,
        NextTransitionRequest nextTransition)
    {
        return new WorkflowExecutionContext
        {
            Domain = currentContext.Domain,
            InstanceId = currentContext.InstanceId.ToString(),
            WorkflowKey = currentContext.WorkflowKey,
            WorkflowVersion = currentContext.Workflow.Version,
            TransitionKey = nextTransition.TransitionKey,
            TriggerType = TriggerType.Automatic,
            Mode = ExecMode.Sync,
            CallerMode = currentContext.CallerMode,
            Actor = ExecutionActor.System,
            CorrelationId = currentContext.CorrelationId,
            CausationId = currentContext.ExecutionChainId,
            RequestedAt = DateTimeOffset.UtcNow,
            Headers = currentContext.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            RouteValues = currentContext.RouteValues.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Execution = new ExecutionInfo
            {
                ExecutionChainId = currentContext.ExecutionChainId,
                ChainDepth = currentContext.ChainDepth + 1,
                ResumeFrom = null
            },
            IsReentry = true,
            EnqueueContinuations = false,
            IsPreReserved = currentContext.IsPreReserved,
            SubflowChainReserved = currentContext.SubflowChainReserved,
            OwnsStatus = currentContext.OwnsStatus,
            Termination = currentContext.Termination,
            IsErrorBoundaryTransition = string.Equals(nextTransition.Reason, TransitionRequestReasons.ErrorBoundary, StringComparison.OrdinalIgnoreCase)
        };
    }
}
