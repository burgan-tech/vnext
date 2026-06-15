namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Determines whether a transition is "reserved" relative to instance locking.
/// All reserved transitions acquire their own type-specific lock that is independent
/// of the main flow lock, so they can run even while another transition holds the instance lock.
/// </summary>
public interface IReservedTransitionResolver
{
    /// <summary>
    /// Returns <c>true</c> if the transition is reserved (cancel, exit, updateData, timeout transition,
    /// subflow resume, or shared transition).
    /// </summary>
    /// <param name="context">The transition execution context.</param>
    bool IsReserved(TransitionExecutionContext context);

    /// <summary>
    /// Returns the type-specific lock key for this reserved transition.
    /// This key is independent from <see cref="TransitionExecutionContext.LockKey"/>
    /// so the reserved transition never conflicts with the main flow lock, while still
    /// serializing concurrent requests of the same reserved type per instance.
    /// </summary>
    /// <param name="context">The transition execution context.</param>
    string GetOwnLockKey(TransitionExecutionContext context);
}
