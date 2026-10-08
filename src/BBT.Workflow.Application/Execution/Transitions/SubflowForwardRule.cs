using BBT.Workflow.Definitions.Specifications;

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
    /// not a parent shared transition available in the parent's current state (that one runs on the
    /// parent), and it is not updateData (always executed on the instance it targets) nor cancel/exit
    /// (the preflight step short-circuits them to the parent's own CreateTransition, past the forward
    /// order, so they are recorded — and must be swapped — on the parent).
    /// <para>
    /// The shared-transition rule is the SubFlow proxy's
    /// (<see cref="SubFlowBypassSpecification.IsParentSharedTransitionAndAvailable(Definitions.Workflow, string, string?)"/>),
    /// so the intake proxy, this step and authorize agree: a shared transition NOT available in the
    /// current state is forwarded like any other key, never run on the parent with its validations
    /// bypassed.
    /// </para>
    /// </summary>
    public static bool WillForward(TransitionExecutionContext context)
        => (context.Instance.HasActiveSubFlow || context.Instance.Subflow != null)
           && !SubFlowBypassSpecification.IsParentSharedTransitionAndAvailable(
               context.Workflow, context.TransitionKey, context.Current?.Key)
           && !context.IsUpdateDataTransition()
           && !context.IsCancelTransition()
           && !context.IsExitTransition();
}
