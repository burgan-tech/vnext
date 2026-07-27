using BBT.Aether.Results;

namespace BBT.Workflow.Execution.PostCommit;

/// <summary>
/// Coordinates post-commit jobs after the transition lock and transaction barrier.
/// </summary>
public interface IPostCommitTransitionCoordinator
{
    /// <summary>
    /// Consumes and executes pending jobs, then decides whether parent execution continues inline.
    /// </summary>
    Task<Result<PostCommitCoordinationResult>> CoordinateAsync(
        TransitionExecutionContext sourceContext,
        CancellationToken cancellationToken);
}
