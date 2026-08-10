using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Applies the common resting-state mutations used both inside the transition pipeline and after
/// a post-commit handoff has reloaded authoritative parent state.
/// </summary>
internal static class TransitionSettlement
{
    public static async Task ApplyAsync(
        TransitionExecutionContext context,
        InstanceStatus? resolvedStatus,
        bool scheduleNotification,
        IInstanceRepository instanceRepository,
        IStateNotificationScheduler stateNotificationScheduler,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (context.Instance.IsBusy &&
            resolvedStatus is not null &&
            context.Target?.SubType != StateSubType.Busy &&
            !context.Instance.ActiveCorrelations.Any(c =>
                c.SubFlowType.Equals(SubFlowType.SubFlow) && !c.IsCompleted))
        {
            context.Instance.Active();
            logger.LogDebug(
                "Instance {InstanceId} resolved to Active after chain settlement",
                context.InstanceId);

            await instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);
        }

        if (scheduleNotification && context.Target?.HasStateNotifications == true)
        {
            await stateNotificationScheduler.ScheduleAsync(context, cancellationToken);
            logger.LogDebug(
                "State notification scheduled for instance {InstanceId} in state {StateKey}",
                context.InstanceId,
                context.Target.Key);
        }
    }
}
