using BBT.Aether.Results;
using BBT.Workflow.Execution;

namespace BBT.Workflow.Definitions.Specifications;

/// <summary>
/// Base specification interface for transition validation rules.
/// Follows DDD Specification Pattern for composable, testable business rules.
/// Each specification encapsulates a single validation concern that can be composed with others.
/// </summary>
public interface ITransitionSpecification
{
    /// <summary>
    /// Determines if this specification is applicable for the given context.
    /// Allows conditional application of specifications based on context state.
    /// </summary>
    /// <param name="context">The transition execution context to check</param>
    /// <returns>True if this specification should be evaluated; false to skip</returns>
    bool IsApplicable(TransitionExecutionContext context);
    
    /// <summary>
    /// Validates the transition execution context against this specification's rules.
    /// Returns Result.Ok() if the specification is satisfied, Result.Fail() with error details otherwise.
    /// </summary>
    /// <param name="context">The transition execution context to validate</param>
    /// <returns>Result indicating validation success or failure with detailed error information</returns>
    Result IsSatisfiedBy(TransitionExecutionContext context);
    
    /// <summary>
    /// Gets the execution priority (lower values execute first).
    /// Allows control of validation order for bypass specifications and dependencies.
    /// Bypass specifications (Resume, Active SubFlow) should have low priority values.
    /// </summary>
    int Priority { get; }
    
    /// <summary>
    /// Gets the specification name for logging and diagnostics.
    /// Defaults to the type name.
    /// </summary>
    string Name => GetType().Name;
}
