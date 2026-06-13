using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that detects cancel and exit transitions and short-circuits to HandleFinishStep.
/// This step runs early (Preflight order) to skip normal transition processing for cancellation or exit.
/// Also enforces the chain-token gate (S6): foreign transitions arriving while an instance is Busy
/// with an active auto-chain (token mismatch) are rejected, unless they are reserved.
/// </summary>
public sealed class HandleCancelPreflightStep(
    IReservedTransitionResolver reservedTransitionResolver,
    IOptions<WorkflowExecutionOptions> executionOptions,
    ILogger<HandleCancelPreflightStep> logger) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.Preflight;

    /// <inheritdoc />
    [Trace]
    public Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context, CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(HandleCancelPreflightStep)}");

        // Chain-token gate: reject foreign transitions while an auto-chain owns the (Busy) instance.
        // The chain's own continuations carry the matching token; reserved transitions are exempt.
        if (executionOptions.Value.StrictChainTokenGate
            && context.Instance.IsBusy
            && context.Instance.ChainToken.HasValue
            && !reservedTransitionResolver.IsReserved(context)
            && !(context.ChainToken.HasValue && context.Instance.MatchesChain(context.ChainToken.Value)))
        {
            logger.ForeignChainTransitionRejected(context.TransitionKey, context.InstanceId);
            return Task.FromResult(Result<StepOutcome>.Fail(
                WorkflowErrors.InstanceLockConflict(context.InstanceId)));
        }

        var isCancelTransition = context.IsCancelTransition();
        var isExitTransition = context.IsExitTransition();

        // Skip if not a cancel or exit transition
        if (!isCancelTransition && !isExitTransition)
        {
            return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Railway chain: Log detection -> Validate -> Create skip outcome
        var result = Result.Ok(context)
            .Tap(_ => LogTransitionDetected(context, isCancelTransition))
            .Ensure(
                ctx => !ctx.Instance.IsCompleted,
                CreateAlreadyCompletedError(context, isCancelTransition))
            .Tap(_ => LogSkipToFinish(context, isCancelTransition))
            .Map(_ => CreateSkipOutcome());

        return Task.FromResult(result);
    }

    /// <summary>
    /// Logs when a cancel or exit transition is detected.
    /// </summary>
    private void LogTransitionDetected(TransitionExecutionContext context, bool isCancelTransition)
    {
        if (isCancelTransition)
            logger.CancelTransitionDetected(context.InstanceId);
        else
            logger.ExitTransitionDetected(context.InstanceId);
    }

    /// <summary>
    /// Logs when skipping to finish step.
    /// </summary>
    private void LogSkipToFinish(TransitionExecutionContext context, bool isCancelTransition)
    {
        if (isCancelTransition)
            logger.CancelSkipToFinish(context.InstanceId);
        else
            logger.ExitSkipToFinish(context.InstanceId);
    }

    /// <summary>
    /// Creates error for already completed instance.
    /// </summary>
    private Error CreateAlreadyCompletedError(TransitionExecutionContext context, bool isCancelTransition)
    {
        if (isCancelTransition)
            logger.CancelInstanceAlreadyCompleted(context.InstanceId, context.Instance.Status.Description);
        else
            logger.ExitInstanceAlreadyCompleted(context.InstanceId, context.Instance.Status.Description);
        
        return ExecutionErrors.InstanceAlreadyCompleted(context.InstanceId, context.Instance.Status.Description);
    }

    /// <summary>
    /// Creates outcome to skip to CreateTransition step.
    /// </summary>
    private static StepOutcome CreateSkipOutcome()
    {
        return new StepOutcome
        {
            SkipToOrder = LifecycleOrder.CreateTransition
        };
    }
}
