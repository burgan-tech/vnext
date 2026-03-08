using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Definitions.Specifications;

/// <summary>
/// When the instance has an active SubFlow and the requested transition is a parent shared transition,
/// the transition's target must be $self so the parent state does not change (subflow has not finished).
/// Any other target results in validation failure.
/// </summary>
public sealed class SharedTransitionTargetSelfWhenInSubFlowSpecification : ITransitionSpecification
{
    /// <inheritdoc />
    /// <summary>
    /// Runs after SubFlowBypassSpecification (20) but before SharedTransitionAvailabilitySpecification (60).
    /// Only applies when we are executing a parent shared transition while in active SubFlow.
    /// </summary>
    public int Priority => 25;

    /// <inheritdoc />
    /// <summary>
    /// Applicable when the instance has an active SubFlow and the requested transition is a parent shared transition.
    /// </summary>
    public bool IsApplicable(TransitionExecutionContext context)
        => context.Instance.HasActiveSubFlow &&
           context.Workflow.FindSharedTransition(context.TransitionKey) != null;

    /// <inheritdoc />
    /// <summary>
    /// Validates that the shared transition's target is $self. Otherwise returns failure.
    /// </summary>
    public Result IsSatisfiedBy(TransitionExecutionContext context)
    {
        if (context.Transition == null)
        {
            return Result.Fail(Error.NotFound(
                "TransitionNotFound",
                $"Transition '{context.TransitionKey}' not found"));
        }

        if (context.Transition.Target == WellKnownStateKeys.Self)
            return Result.Ok();

        return Result.Fail(WorkflowErrors.SharedTransitionTargetMustBeSelfWhenInSubFlow(
            context.TransitionKey,
            context.Transition.Target));
    }
}
