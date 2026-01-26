using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that detects cancel and exit transitions and short-circuits to HandleFinishStep.
/// This step runs early (Preflight order) to skip normal transition processing for cancellation or exit.
/// </summary>
public sealed class HandleCancelPreflightStep(
    ILogger<HandleCancelPreflightStep> logger) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.Preflight;

    /// <inheritdoc />
    [Trace]
    public Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context, CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(HandleCancelPreflightStep)}");

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
