using BBT.Workflow.Execution.Pipeline;

namespace BBT.Workflow.Execution;

/// <summary>
/// Identifies reserved transitions for instance locking behavior.
/// Cancel, exit, updateData, and timeout transitions bypass locking; subflow resume
/// is reserved but acquires an independent lock scope.
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
    public bool RequiresOwnLock(TransitionExecutionContext context)
        => context.Directives.IsSubFlowResume;
}
