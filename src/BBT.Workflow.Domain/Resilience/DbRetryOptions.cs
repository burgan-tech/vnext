namespace BBT.Workflow.Resilience;

/// <summary>
/// Configuration for bounded application-level retry of genuinely transient DB
/// connection faults (excludes pool exhaustion / saturation, which must never be retried).
/// </summary>
public sealed class DbRetryOptions
{
    /// <summary>
    /// Configuration section name
    /// </summary>
    public const string SectionName = "DbRetry";

    /// <summary>
    /// Maximum retry attempts (default: 3). Keep small to avoid amplifying saturation.
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Base delay between retries in milliseconds (default: 100).
    /// </summary>
    public int BaseDelayMilliseconds { get; set; } = 100;

    /// <summary>
    /// Maximum delay cap in milliseconds for exponential backoff (default: 2000).
    /// </summary>
    public int MaxDelayMilliseconds { get; set; } = 2000;

    /// <summary>
    /// Add jitter to retry delays to prevent thundering herd (default: true).
    /// </summary>
    public bool UseJitter { get; set; } = true;
}
