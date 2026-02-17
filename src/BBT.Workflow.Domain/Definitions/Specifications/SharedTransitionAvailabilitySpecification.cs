using BBT.Aether.Results;
using BBT.Workflow.Execution;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Definitions.Specifications;

/// <summary>
/// Specification for validating SharedTransition availability in the current state.
/// SharedTransitions have an optional AvailableIn list that restricts which states they can be executed from.
/// If AvailableIn is empty or null, the transition is available in all states.
/// </summary>
public sealed class SharedTransitionAvailabilitySpecification : ITransitionSpecification
{
    /// <inheritdoc />
    /// <summary>
    /// Medium-low priority - executes after state transition list checks.
    /// </summary>
    public int Priority => 60;
    
    /// <inheritdoc />
    /// <summary>
    /// Applicable only for SharedTransitions.
    /// </summary>
    public bool IsApplicable(TransitionExecutionContext context)
    {
        var sharedTransition = context.Workflow.FindSharedTransition(context.TransitionKey);
        return sharedTransition != null;
    }
    
    /// <inheritdoc />
    /// <summary>
    /// Validates that the current state is in the SharedTransition's AvailableIn list.
    /// If AvailableIn is empty, the transition is available in all states.
    /// </summary>
    public Result IsSatisfiedBy(TransitionExecutionContext context)
    {
        var transition = context.Transition;
        
        if (transition == null)
        {
            return Result.Fail(Error.NotFound(
                "TransitionNotFound",
                $"Transition '{context.TransitionKey}' not found"));
        }
        
        // Error-boundary-requested transitions are allowed from any state
        if (context.IsErrorBoundaryTransition)
            return Result.Ok();
        
        // If AvailableIn is empty or null, transition is available in all states
        if (transition.AvailableIn == null || !transition.AvailableIn.Any())
        {
            return Result.Ok();
        }
        
        // Check if current state is in AvailableIn list
        if (transition.AvailableIn.Contains(context.Current.Key))
        {
            return Result.Ok();
        }
        
        // Current state not in AvailableIn list
        return Result.Fail(WorkflowErrors.SharedTransitionNotAvailableInState(
            transition.Key, 
            context.Current.Key));
    }
}
