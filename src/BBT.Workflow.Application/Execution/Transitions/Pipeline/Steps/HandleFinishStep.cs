using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that handles workflow finishing.
/// Manages workflow completion when the target state type is Finish.
/// This step runs after all other pipeline steps to ensure proper completion.
/// Uses Result pattern for exception-free error handling.
/// SubItem completion events are now published as domain events via Instance.Complete().
/// </summary>
public sealed class HandleFinishStep(
    IInstanceRepository instanceRepository,
    ILogger<HandleFinishStep> logger) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.Finish;
    
    /// <inheritdoc />
    [Trace]
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(HandleFinishStep)}");
        
        // Check applicability - skip if not finish scenario
        if (!IsFinishScenario(context))
        {
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Railway chain: Update status -> Extract events -> Persist -> Mark finish
        return await Result.Ok(context)
            .Tap(UpdateInstanceStatus)
            .Tap(ctx => ctx.ExtractAndDeferInstanceEvents())
            .TapAsync(ctx => instanceRepository.UpdateAsync(ctx.Instance, true, cancellationToken))
            .Tap(ctx => ctx.Items["IsFinishState"] = true)
            .Map(_ => StepOutcome.Continue());
    }

    /// <summary>
    /// Determines if this is a finish scenario (cancel, exit, or finish state).
    /// </summary>
    private bool IsFinishScenario(TransitionExecutionContext context)
    {
        var isCancelTransition = context.IsCancelTransition();
        var isExitTransition = context.IsExitTransition();
        
        // Cancel and Exit transitions always go through finish
        if (isCancelTransition || isExitTransition)
        {
            return true;
        }

        // Check for null target or non-finish state
        if (context.Target == null)
        {
            logger.TargetStateNull(context.InstanceId);
            return false;
        }

        return context.Target.StateType == StateType.Finish;
    }

    /// <summary>
    /// Updates instance status based on transition type.
    /// </summary>
    private void UpdateInstanceStatus(TransitionExecutionContext context)
    {
        if (context.IsCancelTransition())
        {
            logger.InstanceCanceling(context.Instance.Id);
            context.Instance.Cancel(context.Domain);
        }
        else
        {
            logger.InstanceCompleting(context.Instance.Id);
            context.Instance.Complete(context.Domain);
        }
    }
}
