namespace BBT.Workflow.Execution.Transitions;

/// <summary>
/// When a request on a parent is relayed to its active SubFlow — shared by
/// <c>ForwardToActiveSubflowStep</c> and the x-storage file admission, so both agree on which
/// instance records (and therefore swaps) the payload.
/// </summary>
public static class SubflowForwardRule
{
    /// <summary>
    /// True when the parent will forward this request: it has an active subflow, the transition is
    /// not a parent shared transition (one available in the parent's current state runs on the
    /// parent), and it is not updateData (always executed on the instance it targets).
    /// </summary>
    public static bool WillForward(TransitionExecutionContext context)
        => (context.Instance.HasActiveSubFlow || context.Instance.Subflow != null)
           && !(context.Transition != null && context.Workflow.FindSharedTransition(context.TransitionKey) != null)
           && !context.IsUpdateDataTransition();
}
