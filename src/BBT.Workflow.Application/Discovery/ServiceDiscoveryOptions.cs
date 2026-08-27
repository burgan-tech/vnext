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
    /// Discovery API endpoint template for resolving a single domain endpoint.
    /// <c>{0}</c> = domain name, URL-encoded by the resolver before formatting.
    /// <para>
    /// Default: <c>"/discovery/functions/domain-lookup?key={0}"</c> - the discovery domain's
    /// <c>domain-lookup</c> function, which serves the registration through its own read-through
    /// cache (so a hit never touches the instance store) and answers 404 for an unregistered
    /// domain. It returns the same <c>{ "data": { ... }, "eTag": "..." }</c> envelope as the
    /// built-in instance <c>data</c> function.
    /// </para>
    /// <para>
    /// The previous value, <c>"/discovery/workflows/domain/instances/{0}/functions/data"</c>,
    /// still works: it reads the instance directly (no cache) and supports <c>304 Not Modified</c>
    /// on a conditional request. The resolver handles both - it falls back to comparing the
    /// response body's eTag when the endpoint does not answer 304.
    /// </para>
    /// </summary>
    public string DiscoveryEndpointTemplate { get; set; } = "/discovery/functions/domain-lookup?key={0}";

    /// <summary>
    /// Request header used to carry the caller's W3C trace id (32-hex <c>trace.id</c>) to the
    /// discovery service on every registry call. Defaults to
    /// <see cref="BBT.Workflow.Logging.TelemetryConstants.HeaderNames.TraceId"/> (<c>X-Trace-Id</c>).
    /// Set to an empty string to stop sending it.
    /// <para>
    /// The full W3C trace context (<c>traceparent</c>/<c>tracestate</c>) is already propagated by
    /// the HTTP stack; this flat header only makes the trace id directly loggable on the discovery
    /// side through a header enricher.
    /// </para>
    /// </summary>
    public string TraceIdHeader { get; set; } = BBT.Workflow.Logging.TelemetryConstants.HeaderNames.TraceId;

    /// <summary>
    /// Gets or sets whether SSL certificate validation is enabled.
    /// When set to false, SSL certificate errors will be ignored (useful for development environments).
    /// Default is true for security reasons.
    /// </summary>
    public bool ValidateSsl { get; set; } = true;
}
