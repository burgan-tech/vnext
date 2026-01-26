namespace BBT.Workflow.Execution.Bindings;

/// <summary>
/// Binding configuration for GetInstances task execution.
/// Used to retrieve a list of instance data from a remote domain's data function.
/// </summary>
public sealed class GetInstancesBinding
{
    /// <summary>
    /// Target domain where the workflow resides.
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// Target workflow key.
    /// </summary>
    public required string Workflow { get; init; }

    /// <summary>
    /// Page number for pagination (1-based).
    /// </summary>
    public int Page { get; init; } = 1;

    /// <summary>
    /// Page size for pagination.
    /// </summary>
    public int PageSize { get; init; } = 10;

    /// <summary>
    /// Sort field and direction (e.g., "-CreatedAt" for descending).
    /// </summary>
    public string? Sort { get; init; }

    /// <summary>
    /// Filter expressions to apply to the query.
    /// </summary>
    public string[]? Filter { get; init; }

    /// <summary>
    /// Whether to use Dapr service invocation instead of direct HTTP.
    /// </summary>
    public bool UseDapr { get; init; } = false;

    /// <summary>
    /// Whether to validate SSL certificates.
    /// </summary>
    public bool ValidateSSL { get; init; } = true;

    /// <summary>
    /// Resolved base URL for HTTP requests.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Dapr application ID for service invocation.
    /// </summary>
    public string? DaprAppId { get; init; }
}
