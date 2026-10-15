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
        => IsParentSharedTransitionAndAvailable(context.Workflow, context.TransitionKey, context.Current.Key);

    /// <summary>
    /// Context-free form of the same rule, for callers that decide before any execution context
    /// exists (the SubFlow proxy at transition intake): true when <paramref name="transitionKey"/>
    /// names a shared transition of <paramref name="workflow"/> that is available in
    /// <paramref name="currentState"/>, so it runs on the parent instead of being forwarded.
    /// </summary>
    public static bool IsParentSharedTransitionAndAvailable(Workflow workflow, string transitionKey, string? currentState)
    {
        var sharedTransition = workflow.FindSharedTransition(transitionKey);
        if (sharedTransition == null || currentState is null)
            return false;

        return sharedTransition.IsAvailableInState(currentState);
    }
}
