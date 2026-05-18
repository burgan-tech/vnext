namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Determines whether a transition is "reserved" relative to instance locking.
/// Most reserved transitions (cancel, exit, updateData, timeout) bypass the lock entirely.
/// Subflow resume is reserved but uses its own lock scope via <see cref="RequiresOwnLock"/>.
/// </summary>
public interface IReservedTransitionResolver
{
    /// <summary>
    /// Returns <c>true</c> if the transition is reserved (cancel, exit, updateData, timeout transition, or subflow resume).
    /// </summary>
    /// <param name="context">The transition execution context.</param>
    bool IsReserved(TransitionExecutionContext context);

    /// <summary>
    /// Returns <c>true</c> when the reserved transition must acquire its own instance lock scope
    /// instead of bypassing locking (subflow resume only).
    /// </summary>
    /// <param name="context">The transition execution context.</param>
    bool RequiresOwnLock(TransitionExecutionContext context);
}
