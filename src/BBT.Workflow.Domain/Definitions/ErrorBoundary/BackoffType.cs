namespace BBT.Workflow.Definitions;

/// <summary>
/// Defines the backoff strategy for retry operations.
/// </summary>
public enum BackoffType
{
    /// <summary>
    /// Fixed delay between retries. Each retry waits the same amount of time.
    /// </summary>
    Fixed = 0,

    /// <summary>
    /// Exponential backoff between retries. Each retry waits longer than the previous.
    /// Delay = InitialDelay * (BackoffMultiplier ^ attemptNumber)
    /// </summary>
    Exponential = 1
}

