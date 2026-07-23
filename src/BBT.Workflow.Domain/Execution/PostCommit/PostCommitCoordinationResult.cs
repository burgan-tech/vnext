using BBT.Aether.Results;

namespace BBT.Workflow.Execution.PostCommit;

/// <summary>
/// Describes the orchestration decision made after post-commit work has run.
/// </summary>
/// <param name="SourceContext">The transition context that reached the post-commit barrier.</param>
/// <param name="NextContext">A fresh inline continuation context, when parent execution continues.</param>
/// <param name="FaultRequest">A request for fresh-state fault recovery.</param>
/// <param name="Error">The post-commit error associated with a fault request.</param>
public sealed record PostCommitCoordinationResult(
    TransitionExecutionContext SourceContext,
    WorkflowExecutionContext? NextContext,
    PostCommitFaultRequest? FaultRequest,
    Error? Error);
