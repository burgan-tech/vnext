using BBT.Aether.Results;

namespace BBT.Workflow.Execution.PostCommit;

/// <summary>
/// Handler interface for processing post-commit jobs.
/// Each job type has its own handler implementation.
/// Handlers are resolved via DI and executed after the distributed lock is released.
/// </summary>
/// <typeparam name="TJob">The type of post-commit job to handle.</typeparam>
public interface IPostCommitHandler<in TJob> where TJob : IPostCommitJob
{
    /// <summary>
    /// Handles the post-commit job.
    /// </summary>
    /// <param name="job">The job containing the data needed for execution.</param>
    /// <param name="context">The transition execution context.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A Result indicating success or failure.</returns>
    Task<Result> HandleAsync(TJob job, TransitionExecutionContext context, CancellationToken cancellationToken);
}

