using BBT.Aether.Results;
using BBT.Workflow.Execution;

namespace BBT.Workflow.Definitions.Specifications;

/// <summary>
/// Specification for active SubFlow bypass scenario.
/// When a parent instance has an active SubFlow, transition requests are forwarded
/// to the child SubFlow unless the transition is a parent shared transition available in the current state.
/// When applicable, this specification bypasses all validations (request will be forwarded to child).
/// </summary>
public sealed class SubFlowBypassSpecification : ITransitionSpecification
{
    /// <inheritdoc />
    /// <summary>
    /// Second highest priority - executes after Resume but before other validations.
    /// </summary>
    public int Priority => 20;

    /// <inheritdoc />
    /// <summary>
    /// Applicable when the parent has an active SubFlow and the requested transition is NOT
    /// a parent shared transition available in the current state. When it is a parent shared
    /// transition (and available), we do not bypass so it executes on the parent.
    /// </summary>
    public bool IsApplicable(TransitionExecutionContext context)
        => context.Instance.HasActiveSubFlow && !IsParentSharedTransitionAndAvailable(context);

    /// <inheritdoc />
    /// <summary>
    /// Always returns Ok to bypass all validations.
    /// The transition will be forwarded to the active SubFlow instance.
    /// </summary>
    public Result IsSatisfiedBy(TransitionExecutionContext context)
    {
        // Active SubFlow exists and not a parent shared transition - bypass all validations (request will be forwarded to child)
        // Parent instance acts as a proxy while SubFlow is active
        return Result.Ok();
    }

    /// <summary>
    /// Returns true if the requested transition is a parent shared transition and is available in the current state.
    /// Aligned with SharedTransitionAvailabilitySpecification logic.
    /// </summary>
    private static bool IsParentSharedTransitionAndAvailable(TransitionExecutionContext context)
    {
        var sharedTransition = context.Workflow.FindSharedTransition(context.TransitionKey);
        if (sharedTransition == null)
            return false;

        // If AvailableIn is empty or null, transition is available in all states
        if (sharedTransition.AvailableIn == null || !sharedTransition.AvailableIn.Any())
            return true;

        return sharedTransition.AvailableIn.Contains(context.Current.Key);
    }
}
