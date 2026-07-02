using BBT.Workflow.Execution.Pipeline;

namespace BBT.Workflow.Execution;

/// <summary>
/// Identifies reserved transitions for instance locking behavior.
/// All reserved transitions (cancel, exit, updateData, timeout, subflow resume, and
/// shared transitions) acquire their own type-specific lock key, independent of the main
/// flow lock, so they can proceed even when the instance lock is held by a concurrent
/// normal transition (e.g., sync subflow resume inside post-commit, or a shared transition
/// triggered against a busy parent flow).
/// </summary>
public sealed class ReservedTransitionResolver : IReservedTransitionResolver
{
    /// <inheritdoc />
    public bool IsReserved(TransitionExecutionContext context)
    {
        return context.IsCancelTransition()
            || context.IsExitTransition()
            || context.IsUpdateDataTransition()
            || context.Directives.IsTimeoutTransition
            || context.Directives.IsSubFlowResume
            || context.Directives.IsLongPollAckResume
            || context.IsSharedTransition();
    }

    /// <inheritdoc />
    public string GetOwnLockKey(TransitionExecutionContext context)
    {
        if (context.Directives.IsSubFlowResume)
        {
            // Per-sub-instance key: a nested sync resume must not collide with the outer
            // resume chain that is still holding the shared key for this parent.
            return context.Directives.SubFlowResumeInstanceId is { } subId
                ? $"{context.LockKey}:resume:{subId:N}"
                : context.LockKey + ":resume";
        }
        if (context.Directives.IsLongPollAckResume)   return context.LockKey + ":lpack";
        if (context.IsCancelTransition())           return context.LockKey + ":cancel";
        if (context.IsExitTransition())             return context.LockKey + ":exit";
        if (context.IsUpdateDataTransition())       return context.LockKey + ":updatedata";
        if (context.Directives.IsTimeoutTransition) return context.LockKey + ":timeout";
        if (context.IsSharedTransition())           return context.LockKey + ":shared";
        return context.LockKey + ":reserved";
    }
}
