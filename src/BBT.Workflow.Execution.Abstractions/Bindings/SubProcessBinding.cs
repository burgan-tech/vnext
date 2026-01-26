using System.Text.Json;

namespace BBT.Workflow.Execution.Bindings;

/// <summary>
/// Binding configuration for SubProcess task execution.
/// Used to start a subprocess workflow instance in a remote domain.
/// Note: Correlation is handled by the orchestration layer, not the execution layer.
/// </summary>
public sealed class SubProcessBinding
{
    /// <summary>
    /// Target domain where the subprocess workflow resides.
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// Target workflow key to start as subprocess.
    /// </summary>
    public required string Workflow { get; init; }

    /// <summary>
    /// Optional workflow version.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Pre-generated instance ID for the subprocess.
    /// </summary>
    public required Guid InstanceId { get; init; }

    /// <summary>
    /// Callback app ID for subprocess completion notification.
    /// </summary>
    public string? Callback { get; init; }
    
    /// <summary>
    /// Key of the instance to start
    /// </summary>
    public string? Key { get; set; }

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
    /// Extra properties for subprocess metadata.
    /// </summary>
    public Dictionary<string, object>? ExtraProperties { get; init; }

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

