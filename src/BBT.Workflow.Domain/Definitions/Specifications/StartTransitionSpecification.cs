using BBT.Aether.Results;
using BBT.Workflow.Execution;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Definitions.Specifications;

/// <summary>
/// Specification for validating StartTransition execution.
/// StartTransition is special - it can only be executed from Initial state.
/// When an instance is created via StartAsync, it starts in Initial state,
/// and StartTransition moves it to the next state.
/// </summary>
public sealed class StartTransitionSpecification : ITransitionSpecification
{
    /// <inheritdoc />
    /// <summary>
    /// Lowest priority - executes last after all other checks.
    /// </summary>
    public int Priority => 70;
    
    /// <inheritdoc />
    /// <summary>
    /// Applicable only when executing the StartTransition.
    /// </summary>
    public bool IsApplicable(TransitionExecutionContext context)
        => context.TransitionKey == context.Workflow.StartTransition.Key;
    
    /// <inheritdoc />
    /// <summary>
    /// Validates that StartTransition can only be executed from Initial state.
    /// Instance must be in Initial state to execute StartTransition.
    /// </summary>
    public Result IsSatisfiedBy(TransitionExecutionContext context)
    {
        // StartTransition can only be executed from Initial state
        if (context.Current.StateType == StateType.Initial)
        {
            return Result.Ok();
        }
        
        // Instance is not in Initial state - StartTransition not allowed
        return Result.Fail(WorkflowErrors.StartTransitionNotFromInitialState(
            context.InstanceId, 
            context.Current.Key,
            context.Current.StateType));
    }
}
