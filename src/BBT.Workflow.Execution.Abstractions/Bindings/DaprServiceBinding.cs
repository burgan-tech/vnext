namespace BBT.Workflow.Execution.Bindings;

/// <summary>
/// Binding configuration for Dapr service invocation task.
/// </summary>
public sealed class DaprServiceBinding
{
    /// <summary>
    /// The Dapr application ID to invoke.
    /// </summary>
    public required string AppId { get; init; }
    
    /// <summary>
    /// The method name/path to invoke on the target service.
    /// </summary>
    public required string MethodName { get; init; }
    
    /// <summary>
    /// HTTP method for the invocation (defaults to POST).
    /// </summary>
    public string Method { get; init; } = "POST";
    
    /// <summary>
    /// Query string parameters.
    /// </summary>
    public string? QueryString { get; init; }
    
    /// <summary>
    /// Request headers as JSON string.
    /// </summary>
    public string? Headers { get; init; }
    
    /// <summary>
    /// Request body as JSON string.
    /// </summary>
    public string? Body { get; init; }

    /// <summary>
    /// HTTP status codes that should be treated as successful task invocation results.
    /// Supports exact codes such as "400" and wildcard patterns such as "4xx".
    /// </summary>
    public IReadOnlyList<string>? AcceptedStatusCodes { get; init; }
}
