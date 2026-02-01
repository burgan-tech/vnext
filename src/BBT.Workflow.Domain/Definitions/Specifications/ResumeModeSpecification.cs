using BBT.Aether.Results;
using BBT.Workflow.Execution;

namespace BBT.Workflow.Definitions.Specifications;

/// <summary>
/// Specification for Resume mode execution (e.g., SubFlow completion).
/// This is a bypass specification that skips all other validations when applicable.
/// When a SubFlow completes and parent resumes execution, validations were already performed
/// before the SubFlow started, so we skip re-validation.
/// </summary>
public sealed class ResumeModeSpecification : ITransitionSpecification
{
    /// <inheritdoc />
    /// <summary>
    /// Highest priority to execute first and potentially bypass all other specs.
    /// </summary>
    public int Priority => 10;
    
    /// <inheritdoc />
    /// <summary>
    /// Applicable when execution is resuming from SubFlow completion.
    /// </summary>
    public bool IsApplicable(TransitionExecutionContext context)
        => context.Directives.IsSubFlowResume;
    
    /// <inheritdoc />
    /// <summary>
    /// Always returns Ok to bypass all validations.
    /// Resume mode means validations were already performed before SubFlow started.
    /// </summary>
    public Result IsSatisfiedBy(TransitionExecutionContext context)
    {
        // Resume mode - skip all validations (already validated before SubFlow started)
        // This is a bypass specification - always returns Ok to short-circuit validation chain
        return Result.Ok();
    }
}
