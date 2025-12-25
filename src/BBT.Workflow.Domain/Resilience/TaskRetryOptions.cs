namespace BBT.Workflow.Resilience;

/// <summary>
/// Configuration options for task execution retry policies.
/// Used for both Dapr communication errors and HTTP status code based retries.
/// </summary>
public sealed class TaskRetryOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "TaskRetry";

    /// <summary>
    /// Maximum retry attempts (default: 3).
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Initial delay between retries in milliseconds (default: 100ms).
    /// </summary>
    public int RetryDelayMilliseconds { get; set; } = 100;

    /// <summary>
    /// Maximum delay between retries in milliseconds (default: 30000ms = 30s).
    /// </summary>
    public int MaxRetryDelayMilliseconds { get; set; } = 30000;

    /// <summary>
    /// Backoff type for retry delays (default: Exponential).
    /// Options: Constant, Linear, Exponential.
    /// </summary>
    public string BackoffType { get; set; } = "Exponential";

    /// <summary>
    /// Backoff multiplier for exponential backoff (default: 2.0).
    /// </summary>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Enable jitter for retry delays to prevent thundering herd (default: true).
    /// </summary>
    public bool UseJitter { get; set; } = true;

    /// <summary>
    /// HTTP status codes that should trigger a retry.
    /// Default: 408 (Timeout), 429 (Too Many Requests), 500, 502, 503, 504 (Server errors).
    /// </summary>
    public int[] RetryableStatusCodes { get; set; } =
    [
        408, // Request Timeout
        429, // Too Many Requests
        500, // Internal Server Error
        502, // Bad Gateway
        503, // Service Unavailable
        504  // Gateway Timeout
    ];

    /// <summary>
    /// Error codes that should trigger a retry (Dapr, network errors).
    /// </summary>
    public string[] RetryableErrorCodes { get; set; } =
    [
        "Dapr.Invocation",
        "Dapr.ServiceInvocation",
        "Network.Timeout",
        "Network.ConnectionRefused",
        "Http.Timeout",
        "Task.Timeout"
    ];
}

