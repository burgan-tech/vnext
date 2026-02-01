using BBT.Aether.Results;
using BBT.Workflow.Execution;

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
    /// Applicable for Cancel, UpdateData, and Exit transition keys.
    /// </summary>
    public bool IsApplicable(TransitionExecutionContext context)
    {
        var key = context.TransitionKey;
        return key == WellKnownTransitionKeys.Cancel 
            || key == WellKnownTransitionKeys.UpdateData 
            || key == WellKnownTransitionKeys.Exit;
    }
    
    /// <inheritdoc />
    /// <summary>
    /// Well-known transitions are always valid if configured in workflow.
    /// Actual configuration existence is validated during transition resolution phase.
    /// This specification just marks them as valid for state machine policy purposes.
    /// </summary>
    public Result IsSatisfiedBy(TransitionExecutionContext context)
    {
        // Well-known transitions bypass normal state machine rules
        // If the workflow has them configured, they're available from any state
        // Configuration check happens in Workflow.ResolveWellKnownKey()
        return Result.Ok();
    }
}
