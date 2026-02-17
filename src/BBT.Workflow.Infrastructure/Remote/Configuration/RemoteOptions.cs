namespace BBT.Workflow.Remote.Configuration;

/// <summary>
/// Configuration options for remote instance services
/// </summary>
public sealed class RemoteOptions
{
    /// <summary>
    /// Configuration section name
    /// </summary>
    public const string SectionName = "vNextApi";

    /// <summary>
    /// Base URL for the remote workflow API
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// API version to use (default: v1.0)
    /// </summary>
    public string ApiVersion { get; set; } = "1.0";

    /// <summary>
    /// Timeout in seconds for HTTP requests (default: 30 seconds)
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum retry attempts for failed requests (default: 3)
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Retry delay in milliseconds (default: 1000ms)
    /// </summary>
    public int RetryDelayMilliseconds { get; set; } = 1000;

    /// <summary>
    /// Circuit breaker failure threshold (default: 20 for auto-transition scenarios)
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 20;

    /// <summary>
    /// Circuit breaker timeout in seconds (default: 30 seconds)
    /// </summary>
    public int CircuitBreakerTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Enable circuit breaker bypass for internal operations (default: true)
    /// </summary>
    public bool EnableCircuitBreakerBypass { get; set; } = true;

    /// <summary>
    /// Internal operation header name for bypass identification
    /// </summary>
    public string InternalOperationHeader { get; set; } = "X-Internal-Operation";

    /// <summary>
    /// Gets or sets whether SSL certificate validation is enabled.
    /// When set to false, SSL certificate errors will be ignored (useful for development environments).
    /// Default is true for security reasons.
    /// </summary>
    public bool ValidateSsl { get; set; } = true;
} 