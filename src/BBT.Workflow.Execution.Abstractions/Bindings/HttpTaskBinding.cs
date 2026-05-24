namespace BBT.Workflow.Execution.Bindings;

/// <summary>
/// Binding configuration for HTTP task execution.
/// </summary>
public sealed class HttpTaskBinding
{
    /// <summary>
    /// The target URL for the HTTP request.
    /// </summary>
    public required string Url { get; init; }
    
    /// <summary>
    /// HTTP method (GET, POST, PUT, DELETE, etc.).
    /// </summary>
    public string Method { get; init; } = "GET";
    
    /// <summary>
    /// Request headers as JSON string.
    /// </summary>
    public string? Headers { get; init; }
    
    /// <summary>
    /// Request body as JSON string.
    /// </summary>
    public string? Body { get; init; }
    
    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;
    
    /// <summary>
    /// Whether to validate SSL certificates.
    /// </summary>
    public bool ValidateSSL { get; init; } = true;

    /// <summary>
    /// HTTP status codes that should be treated as successful task invocation results.
    /// Supports exact codes such as "400" and wildcard patterns such as "4xx".
    /// </summary>
    public IReadOnlyList<string>? AcceptedStatusCodes { get; init; }
}
