using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that resolves the timeout target state and sets it on the execution context.
/// Runs before CancelScheduledJobs so that OnExit and subsequent steps have access to the
/// correct target state. The actual state change is performed by ChangeStateStep (order 50).
/// </summary>
public sealed class ApplyTimeoutStateStep(
    ILogger<ApplyTimeoutStateStep> logger) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.ApplyTimeoutState;

    /// <inheritdoc />
    [Trace]
    public Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(ApplyTimeoutStateStep)}");

        // Only process this step for timeout transitions
        if (!context.Directives.IsTimeoutTransition)
        {
            return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        if (context.Workflow.Timeout is null)
        {
            logger.TimeoutConfigMissing(context.Workflow.Key);
            return Task.FromResult(Result<StepOutcome>.Fail(
                WorkflowErrors.TimeoutConfigMissing(context.Workflow.Key)));
        }

        // Resolve timeout target state through workflow aggregate (handles $self and other well-known keys)
        var stateResult = context.Workflow.GetState(
            context.Workflow.Timeout.Target,
            context.Instance.GetCurrentState);

        if (!stateResult.IsSuccess)
        {
            return Task.FromResult(Result<StepOutcome>.Fail(stateResult.Error));
        }

        // Set context.Target so that CancelScheduledJobs, OnExit and all subsequent steps
        // know which state the instance is transitioning into.
        // The actual instance.ChangeState() call is deferred to ChangeStateStep (order 50).
        context.Target = stateResult.Value!;

        return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
    }
}
