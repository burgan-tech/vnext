namespace BBT.Workflow.Discovery;

/// <summary>
/// Configuration options for service discovery and domain registration.
/// Used to configure automatic domain registration when the application starts.
/// Each vNext instance will register itself with the central registry endpoint on startup.
/// </summary>
public sealed class ServiceDiscoveryOptions
{
    /// <summary>
    /// Configuration section name for service discovery options.
    /// </summary>
    public const string SectionName = "ServiceDiscovery";

    /// <summary>
    /// Gets or sets whether service discovery is enabled.
    /// When enabled, the application will automatically register itself with the domain registry on startup.
    /// Default is false.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Gets or sets the base URL of the central registry endpoint.
    /// This is the vNext instance that hosts the domain-registration workflow.
    /// HTTP calls will be made to: {BaseUrl}/{Domain}/workflows/domain-registration/instances/start
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the domain name to use in the HTTP call path.
    /// This is the domain where the domain-registration workflow is defined.
    /// Default is "core".
    /// </summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the registry workflow name.
    /// </summary>
    public string RegistryFlow { get; set; } = string.Empty;

    /// <summary>
    /// Timeout in seconds for HTTP requests (default: 30 seconds).
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum retry attempts for failed requests (default: 3).
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>
    /// Retry delay in milliseconds (default: 1000ms).
    /// </summary>
    public int RetryDelayMilliseconds { get; set; } = 1000;

    /// <summary>
    /// Circuit breaker failure threshold before opening (default: 5).
    /// </summary>
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// <summary>
    /// Circuit breaker timeout in seconds (default: 30 seconds).
    /// </summary>
    public int CircuitBreakerTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Enable circuit breaker bypass for internal operations (default: false).
    /// </summary>
    public bool EnableCircuitBreakerBypass { get; set; } = false;

    /// <summary>
    /// Internal operation header name for bypass identification.
    /// </summary>
    public string InternalOperationHeader { get; set; } = "X-Internal-Operation";

    /// <summary>
    /// Cache duration in seconds for discovered endpoints (default: 300 = 5 minutes).
    /// </summary>
    public int DiscoveryCacheSeconds { get; set; } = 300;

    /// <summary>
    /// Discovery API endpoint template for resolving domain endpoints.
    /// {0} = domain name.
    /// Default: "/discovery/workflows/domain/instances/{0}/functions/data"
    /// </summary>
    public string DiscoveryEndpointTemplate { get; set; } = "/discovery/workflows/domain/instances/{0}/functions/data";

    /// <summary>
    /// Gets or sets whether SSL certificate validation is enabled.
    /// When set to false, SSL certificate errors will be ignored (useful for development environments).
    /// Default is true for security reasons.
    /// </summary>
    public bool ValidateSsl { get; set; } = true;
}
