using BBT.Aether.Results;
using BBT.Workflow.Execution;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Definitions.Specifications;

/// <summary>
/// Specification for validating that a transition exists in the current state's transition list.
/// Enforces state machine rules: only transitions defined in the current state can be executed.
/// Exceptions: Well-known transitions, StartTransition, and SharedTransitions (handled separately).
/// </summary>
public sealed class StateTransitionListSpecification : ITransitionSpecification
{
    /// <inheritdoc />
    /// <summary>
    /// Medium priority - executes after actor checks but before shared transition checks.
    /// </summary>
    public int Priority => 50;
    
    /// <inheritdoc />
    /// <summary>
    /// Not applicable for well-known transitions, StartTransition, or SharedTransitions.
    /// These are handled by dedicated specifications.
    /// </summary>
    public bool IsApplicable(TransitionExecutionContext context)
    {
        // Not applicable for well-known transitions (Cancel, UpdateData, Exit)
        if (IsWellKnownTransition(context.TransitionKey))
            return false;
            
        // Not applicable for StartTransition (special case)
        if (context.TransitionKey == context.Workflow.StartTransition.Key)
            return false;
        
        // Not applicable for SharedTransitions (handled by SharedTransitionAvailabilitySpecification)
        if (context.Workflow.FindSharedTransition(context.TransitionKey) != null)
            return false;
            
        return true;
    }
    
    /// <inheritdoc />
    /// <summary>
    /// Validates that the transition exists in the current state's transition list.
    /// </summary>
    public Result IsSatisfiedBy(TransitionExecutionContext context)
    {
        var currentState = context.Current;
        var transitionKey = context.TransitionKey;
        
        // Check if transition is in current state's transition list
        var stateTransition = currentState.FindTransition(transitionKey);
        if (stateTransition != null)
        {
            return Result.Ok();
        }
        
        // Transition not found in current state
        return Result.Fail(WorkflowErrors.TransitionNotAvailableInCurrentState(
            transitionKey, 
            currentState.Key));
    }
    
    /// <summary>
    /// Determines if a transition key is a well-known transition.
    /// </summary>
    private static bool IsWellKnownTransition(string transitionKey)
    {
        return transitionKey == WellKnownTransitionKeys.Cancel
            || transitionKey == WellKnownTransitionKeys.UpdateData
            || transitionKey == WellKnownTransitionKeys.Exit;
    }
}
