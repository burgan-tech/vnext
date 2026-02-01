using BBT.Aether.Results;
using BBT.Workflow.Execution;

namespace BBT.Workflow.Definitions.Specifications;

/// <summary>
/// Specification for active SubFlow bypass scenario.
/// When a parent instance has an active SubFlow, all transition requests are forwarded
/// to the child SubFlow instead of being executed on the parent.
/// This specification bypasses all validations in that case.
/// </summary>
public sealed class SubFlowBypassSpecification : ITransitionSpecification
{
    /// <inheritdoc />
    /// <summary>
    /// Second highest priority - executes after Resume but before other validations.
    /// </summary>
    public int Priority => 20;
    
    /// <inheritdoc />
    /// <summary>
    /// Applicable when the parent instance has an active SubFlow.
    /// </summary>
    public bool IsApplicable(TransitionExecutionContext context)
        => context.Instance.HasActiveSubFlow;
    
    /// <inheritdoc />
    /// <summary>
    /// Always returns Ok to bypass all validations.
    /// The transition will be forwarded to the active SubFlow instance.
    /// </summary>
    public Result IsSatisfiedBy(TransitionExecutionContext context)
    {
        // Active SubFlow exists - bypass all validations (request will be forwarded to child)
        // Parent instance acts as a proxy while SubFlow is active
        return Result.Ok();
    }
}
