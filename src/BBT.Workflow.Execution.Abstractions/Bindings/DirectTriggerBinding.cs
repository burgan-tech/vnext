using System.Text.Json;

namespace BBT.Workflow.Execution.Bindings;

/// <summary>
/// Binding configuration for DirectTrigger task execution.
/// Used to trigger a transition on an existing workflow instance in a remote domain.
/// </summary>
public sealed class DirectTriggerBinding
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
    /// Transition name to execute (required)
    /// </summary>
    public required string TransitionName { get; init; }

    /// <summary>
    /// Target instance ID (optional).
    /// </summary>
    public Guid? InstanceId { get; set; }

    /// <summary>
    ///  Flow key of the target workflow (optional)
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    /// Version of the target workflow (optional)
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Tags of the target workflow (optional)
    /// </summary>
    public string[]? Tags { get; set; }

    /// <summary>
    /// Request body (transition data) as JSON.
    /// </summary>
    public JsonElement? Body { get; init; }

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
    /// Timeout seconds for HTTP requests.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Resolved base URL for HTTP requests.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Dapr application ID for service invocation.
    /// </summary>
    public string? DaprAppId { get; init; }

    /// <summary>
    /// Identifier of the target workflow instance.
    /// </summary>
    public string? Identifier => InstanceId.HasValue ? InstanceId.Value.ToString() : Key;
}

