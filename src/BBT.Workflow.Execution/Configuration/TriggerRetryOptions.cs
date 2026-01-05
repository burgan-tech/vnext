namespace BBT.Workflow.Execution.Configuration;

/// <summary>
/// Configuration options for Trigger Tasks retry and resilience policies.
/// </summary>
public sealed class TriggerRetryOptions
{
    /// <summary>
    /// Configuration section name
    /// </summary>
    public const string SectionName = "TriggerRetry";

    /// <summary>
    /// Maximum retry attempts (default: 3)
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Delay between retries in milliseconds (default: 50ms)
    /// </summary>
    public int RetryDelayMilliseconds { get; set; } = 50;

    /// <summary>
    /// HTTP status codes that should trigger a retry (default: [409])
    /// </summary>
    public int[] RetryOnStatusCodes { get; set; } = [409];

    /// <summary>
    /// Enable jitter for retry delays to prevent thundering herd (default: true)
    /// </summary>
    public bool UseJitter { get; set; } = true;

    /// <summary>
    /// Timeout in seconds for HTTP requests (default: 30)
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Number of failures before the circuit breaker opens (default: 5)
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>
    /// Duration in seconds the circuit breaker stays open (default: 30)
    /// </summary>
    public int CircuitBreakerTimeoutSeconds { get; set; } = 30;
}