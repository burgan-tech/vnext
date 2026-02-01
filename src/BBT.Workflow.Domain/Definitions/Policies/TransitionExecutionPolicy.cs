using BBT.Aether.Results;
using BBT.Workflow.Definitions.Specifications;
using BBT.Workflow.Execution;

namespace BBT.Workflow.Definitions.Policies;

/// <summary>
/// Main policy for validating transition execution using Specification Pattern.
/// Coordinates multiple specifications through composite pattern for modular, testable validation.
/// Replaces the old StateTransitionPolicy with a more extensible DDD-based approach.
/// </summary>
/// <remarks>
/// Validation flow:
/// 1. Bypass checks (Resume, Active SubFlow) - short-circuit if applicable
/// 2. Well-known transitions (Cancel, UpdateData, Exit) - always valid if configured
/// 3. Actor authorization (User/System based on trigger type)
/// 4. State machine rules (transition list, shared transitions, start transition)
/// </remarks>
public sealed class TransitionExecutionPolicy
{
    private readonly CompositeTransitionSpecification _composite;
    
    /// <summary>
    /// Initializes a new instance of TransitionExecutionPolicy.
    /// </summary>
    /// <param name="composite">The composite specification containing all validation rules</param>
    public TransitionExecutionPolicy(CompositeTransitionSpecification composite)
    {
        _composite = composite;
    }
    
    /// <summary>
    /// Validates a transition execution context using all applicable specifications.
    /// Returns Result.Ok() if all validations pass, or Result.Fail() with the first error encountered.
    /// </summary>
    /// <param name="context">The transition execution context to validate</param>
    /// <returns>Result indicating validation success or failure with detailed error information</returns>
    public Result Validate(TransitionExecutionContext context)
    {
        return _composite.IsSatisfiedBy(context);
    }
}
