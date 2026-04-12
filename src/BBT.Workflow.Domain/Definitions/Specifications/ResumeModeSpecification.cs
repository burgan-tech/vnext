using BBT.Aether.Results;
using BBT.Workflow.Execution;

namespace BBT.Workflow.Definitions.Specifications;

/// <summary>
/// Specification for internal pipeline resume scenarios (SubFlow completion, workflow timeout).
/// This is a bypass specification that skips all other validations when applicable.
/// These scenarios use a virtual/empty transition key and are triggered internally by the system,
/// so state-machine and actor validations do not apply.
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
    /// Applicable when execution is resuming from SubFlow completion or triggered by workflow timeout.
    /// </summary>
    public bool IsApplicable(TransitionExecutionContext context)
        => context.Directives.IsSubFlowResume || context.Directives.IsTimeoutTransition;

    /// <inheritdoc />
    /// <summary>
    /// Always returns Ok to bypass all validations.
    /// Internal pipeline resumes are system-triggered and require no external validation.
    /// </summary>
    public Result IsSatisfiedBy(TransitionExecutionContext context)
    {
        // Internal pipeline resume - skip all validations
        // This is a bypass specification - always returns Ok to short-circuit validation chain
        return Result.Ok();
    }
}
