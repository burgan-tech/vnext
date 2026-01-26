using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that clears the busy state when resuming from SubFlow completion.
/// Implements optimizations:
/// 1. Resolves target state first to check SubType
/// 2. Skips status update if target state is Busy SubType (ChangeState will handle it)
/// 3. Implements idempotency - only updates DB if status actually changes
/// </summary>
public sealed class ClearBusyOnResumeStep(
    IInstanceRepository instanceRepository) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.ClearBusyOnResumeStep;

    /// <inheritdoc />
    [Trace]
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(ClearBusyOnResumeStep)}");

        // Only process this step if resuming from SubFlow completion
        if (!context.Directives.IsSubFlowResume)
        {
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Railway chain: Resolve target state first, then conditionally clear busy
        return await Result.Ok(context)
            .Bind(UpdateTargetStateInContext)  // 1. Resolve target state first
            .BindAsync(ctx => ClearBusyIfNeededAsync(ctx, cancellationToken))  // 2. Conditional clear
            .Map(_ => StepOutcome.Continue());
    }

    /// <summary>
    /// Updates target state in context by resolving the current state from workflow definition.
    /// </summary>
    /// <param name="context">Transition execution context</param>
    /// <returns>Result containing the context with resolved target state</returns>
    private static Result<TransitionExecutionContext> UpdateTargetStateInContext(TransitionExecutionContext context)
    {
        return context.Workflow.GetState(context.Instance.GetCurrentState)
            .Map(state =>
            {
                context.Target = state;
                return context;
            });
    }

    /// <summary>
    /// Conditionally clears busy status and updates repository.
    /// Implements two optimizations:
    /// 1. Skip if target state SubType is Busy (ChangeState will handle it)
    /// 2. Skip if instance is already Active (idempotency)
    /// </summary>
    /// <param name="context">Transition execution context with resolved target state</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing the context</returns>
    private async Task<Result<TransitionExecutionContext>> ClearBusyIfNeededAsync(
        TransitionExecutionContext context, 
        CancellationToken cancellationToken)
    {
        var targetState = context.Target;
        
        // Optimization 1: If target state is Busy subtype, ChangeState will handle status
        // No need to set Active here (would be immediately overridden to Busy anyway)
        if (targetState?.SubType == StateSubType.Busy)
        {
            return Result<TransitionExecutionContext>.Ok(context);
        }
        
        // Optimization 2: Idempotency - only update if status actually needs to change
        if (context.Instance is { IsActive: false, IsCompleted: false })
        {
            context.Instance.Active();
            await instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);
        }
        
        return Result<TransitionExecutionContext>.Ok(context);
    }
}
