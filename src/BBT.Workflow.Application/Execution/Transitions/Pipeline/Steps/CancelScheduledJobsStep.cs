using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that cancels scheduled transition jobs for the current state before leaving it.
/// Only cancels jobs for the current state's scheduled transitions, not all instance jobs.
/// Positioned between OnExecute and OnExit to ensure cleanup before state change.
/// </summary>
public sealed class CancelScheduledJobsStep(
    IInstanceCancellationService cancellationService,
    ILogger<CancelScheduledJobsStep> logger) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.CancelScheduledJobs;

    /// <inheritdoc />
    [Trace]
    public async Task<Result<StepOutcome>> ExecuteAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(CancelScheduledJobsStep)}");

        // Only cancel if current state has scheduled transitions
        if (context.Current == null || !HasScheduledTransitions(context.Current))
        {
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Get transition keys for the current state's scheduled transitions
        var scheduledTransitionKeys = context.Current.ScheduledTransitions
            .Select(t => t.Key)
            .ToList();

        var transitionKeysStr = string.Join(", ", scheduledTransitionKeys);

        logger.ScheduledJobsCanceling(
            context.InstanceId,
            context.Current.Key,
            transitionKeysStr);

        // Cancel ONLY the scheduled jobs for this state's transitions
        var result = await cancellationService.ProcessStateTransitionsCancellationAsync(
            context.InstanceId,
            scheduledTransitionKeys,
            cancellationToken);

        if (!result.IsSuccess)
        {
            logger.ScheduledJobsCancellationFailed(
                context.InstanceId,
                transitionKeysStr);
            // Don't fail the transition - log and continue
        }

        return Result<StepOutcome>.Ok(StepOutcome.Continue());
    }

    /// <summary>
    /// Checks if state has scheduled transitions.
    /// </summary>
    private static bool HasScheduledTransitions(State state)
        => state.ScheduledTransitions?.Any() == true;
}
