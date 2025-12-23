namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Tracks retry state for error handling.
/// Stored in TransitionExecutionContext.Items for persistence across retries.
/// </summary>
public sealed class RetryState
{
    /// <summary>
    /// Key used to store retry state in context items.
    /// </summary>
    public const string ContextKey = "ErrorBoundary:RetryState";

    /// <summary>
    /// Creates a key for task-specific retry state.
    /// </summary>
    public static string TaskContextKey(string taskKey) => $"ErrorBoundary:Retry:{taskKey}";

    /// <summary>
    /// The task key being retried.
    /// </summary>
    public string TaskKey { get; init; } = string.Empty;

    /// <summary>
    /// The current retry attempt (1-based).
    /// </summary>
    public int Attempt { get; set; } = 1;

    /// <summary>
    /// The maximum number of retries allowed.
    /// </summary>
    public int MaxRetries { get; init; }

    /// <summary>
    /// Timestamp of the first retry attempt.
    /// </summary>
    public DateTimeOffset FirstAttemptAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Timestamp of the last retry attempt.
    /// </summary>
    public DateTimeOffset LastAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The delay before the next retry.
    /// </summary>
    public TimeSpan NextDelay { get; set; }

    /// <summary>
    /// The last error that triggered a retry.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets a value indicating whether more retries are allowed.
    /// </summary>
    public bool CanRetry => Attempt < MaxRetries;

    /// <summary>
    /// Gets the total elapsed time since the first attempt.
    /// </summary>
    public TimeSpan TotalElapsed => DateTimeOffset.UtcNow - FirstAttemptAt;

    /// <summary>
    /// Increments the attempt counter and updates timing.
    /// </summary>
    public void IncrementAttempt()
    {
        Attempt++;
        LastAttemptAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Creates a new retry state for a task.
    /// </summary>
    public static RetryState Create(string taskKey, int maxRetries)
    {
        return new RetryState
        {
            TaskKey = taskKey,
            MaxRetries = maxRetries
        };
    }
}

