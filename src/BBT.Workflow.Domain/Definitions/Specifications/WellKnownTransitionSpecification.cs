using BBT.Aether.Results;
using BBT.Workflow.Execution;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Definitions.Specifications;

/// <summary>
/// Specification for well-known transitions (Cancel, UpdateData, Exit).
/// These transitions are special and globally available across all states if configured in the workflow.
/// They bypass normal state machine rules like transition list checks.
/// </summary>
public sealed class WellKnownTransitionSpecification : ITransitionSpecification
{
    /// <inheritdoc />
    /// <summary>
    /// Medium priority - executes before state machine checks but after bypass specs.
    /// </summary>
    public int Priority => 30;
    
    /// <inheritdoc />
    /// <summary>
    /// Applicable for Cancel, UpdateData, and Exit — matched by reserved alias <b>or</b> by the key the
    /// workflow configured for them. Matching only the aliases would let a custom-keyed well-known
    /// transition escape this specification while <see cref="StateTransitionListSpecification"/> also
    /// excludes it, leaving it entirely ungated.
    /// </summary>
    public bool IsApplicable(TransitionExecutionContext context)
        => context.Workflow.IsWellKnownTransitionKey(context.TransitionKey);

    /// <inheritdoc />
    /// <summary>
    /// Well-known transitions bypass the state's transition list, but they still honour their own
    /// <c>availableIn</c> restriction: declaring one narrows where it may be executed from.
    /// <para>
    /// Only the state is checked here. Per-state role grants are deliberately not evaluated — the
    /// execution policy has never enforced roles for any transition type; roles gate discovery
    /// (<c>availableTransitions</c>) and the <c>authorize</c> function.
    /// </para>
    /// </summary>
    public Result IsSatisfiedBy(TransitionExecutionContext context)
    {
        // Error-boundary-requested transitions are allowed from any state, matching
        // SharedTransitionAvailabilitySpecification.
        if (context.IsErrorBoundaryTransition)
            return Result.Ok();

        var transition = context.Workflow.ResolveWellKnownTransition(context.TransitionKey);

        // Not configured on this workflow: existence is reported by Workflow.ResolveWellKnownKey()
        // during transition resolution, so nothing to add here.
        if (transition == null)
            return Result.Ok();

        if (transition.IsAvailableInState(context.Current.Key))
            return Result.Ok();

        return Result.Fail(WorkflowErrors.WellKnownTransitionNotAvailableInState(
            transition.Key,
            context.Current.Key));
    }
}
