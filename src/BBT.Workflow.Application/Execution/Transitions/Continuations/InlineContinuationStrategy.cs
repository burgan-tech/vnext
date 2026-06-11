using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Execution.Continuations;

/// <summary>
/// Realizes the auto-chain continuation in-process: builds the next
/// <see cref="WorkflowExecutionContext"/> so the pipeline loop runs the chained
/// transition under the same request and lock scope. This reproduces the original
/// in-loop chaining behavior exactly (sync execution).
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
    /// Identity-only — the full context is rebuilt and validated by the pipeline's context factory.
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
            Execution = new ExecutionInfo
            {
                ExecutionChainId = currentContext.ExecutionChainId,
                ChainDepth = currentContext.ChainDepth + 1,
                ResumeFrom = null
            },
            IsReentry = true,
            IsErrorBoundaryTransition = string.Equals(nextTransition.Reason, TransitionRequestReasons.ErrorBoundary, StringComparison.OrdinalIgnoreCase)
        };
    }
}
