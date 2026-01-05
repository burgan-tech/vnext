using BBT.Aether.Results;

namespace BBT.Workflow.Execution.PostCommit;

/// <summary>
/// Store for tracking post-commit job execution to prevent duplicates.
/// Provides at-most-once execution semantics for idempotent jobs.
/// </summary>
public interface IPostCommitIdempotencyStore
{
    /// <summary>
    /// Attempts to begin processing a job with the given idempotency key.
    /// If the job has already been processed or is in progress, returns false.
    /// </summary>
    /// <param name="key">The idempotency key of the job.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Result containing true if the job should be executed (first time),
    /// false if the job was already processed (duplicate), or error if store operation failed.
    /// </returns>
    Task<Result<bool>> TryBeginAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Marks a job as successfully completed.
    /// </summary>
    /// <param name="key">The idempotency key of the job.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task MarkCompletedAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Marks a job as failed with error details.
    /// </summary>
    /// <param name="key">The idempotency key of the job.</param>
    /// <param name="errorCode">The error code.</param>
    /// <param name="errorMessage">The error message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task MarkFailedAsync(string key, string errorCode, string? errorMessage, CancellationToken cancellationToken);
}

