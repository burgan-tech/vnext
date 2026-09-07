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

    /// <summary>
    /// Re-enables transport-level retry on the MUTATING remote clients
    /// (<c>IRemoteInstanceCommandAppService</c>, <c>IRemoteInstanceRetryAppService</c>).
    /// Default false, and it should stay false.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Emergency reversal only. Those clients carry side-effecting endpoints
    /// (<c>instances/start</c>, <c>internal/subflow-forward</c>, <c>transitions/{key}</c>,
    /// <c>sub/*</c>, <c>busy</c>, <c>child-cancel</c>, <c>complete</c>, <c>longpoll/ack</c>) where
    /// a retried request can duplicate the effect: a second start, or a second subflow forward.
    /// Retry for them belongs to the user-defined error boundary, which is the only layer that
    /// knows whether repeating a given transition is safe.
    /// </para>
    /// <para>
    /// It exists because the retry split is a CODE change and therefore outside the scope of the
    /// <c>ServiceDiscovery:Provider</c> switch — flipping the provider back to <c>http</c> does
    /// not restore the old retry behaviour. This flag is the only way back without a redeploy.
    /// </para>
    /// </remarks>
    public bool EnableRetryOnMutating { get; set; } = false;
} 