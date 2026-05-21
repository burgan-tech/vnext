using BBT.Workflow.Execution.Pipeline;

namespace BBT.Workflow.Execution;

/// <summary>
/// Identifies reserved transitions for instance locking behavior.
/// All reserved transitions acquire their own type-specific lock key, independent of
/// the main flow lock, so they can proceed even when the instance lock is held by a
/// concurrent normal transition (e.g., sync subflow resume inside post-commit).
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
            || context.Directives.IsSubFlowResume;
    }

    /// <inheritdoc />
    public string GetOwnLockKey(TransitionExecutionContext context)
    {
        if (context.Directives.IsSubFlowResume)     return context.LockKey + ":resume";
        if (context.IsCancelTransition())           return context.LockKey + ":cancel";
        if (context.IsExitTransition())             return context.LockKey + ":exit";
        if (context.IsUpdateDataTransition())       return context.LockKey + ":updatedata";
        if (context.Directives.IsTimeoutTransition) return context.LockKey + ":timeout";
        return context.LockKey + ":reserved";
    }
}
