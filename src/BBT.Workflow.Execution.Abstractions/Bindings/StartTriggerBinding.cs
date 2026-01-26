using System.Text.Json;

namespace BBT.Workflow.Execution.Bindings;

/// <summary>
/// Binding configuration for StartTrigger task execution.
/// Used to start a new workflow instance in a remote domain.
/// </summary>
public sealed class StartTriggerBinding
{
    /// <summary>
    /// Target domain where the workflow resides.
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// Target workflow key to start.
    /// </summary>
    public required string Workflow { get; init; }

    /// <summary>
    /// Optional workflow version.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Key of the instance to start.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>
    /// Request body (instance attributes) as JSON.
    /// </summary>
    public JsonElement? Body { get; init; }

    /// <summary>
    /// Tags of the instance to start.
    /// </summary>
    public string[]? Tags { get; init; }

    /// <summary>
    /// Request headers as JSON string.
    /// </summary>
    public string? Headers { get; init; }

    /// <summary>
    /// Whether to execute synchronously.
    /// </summary>
    public bool Sync { get; init; } = true;

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

