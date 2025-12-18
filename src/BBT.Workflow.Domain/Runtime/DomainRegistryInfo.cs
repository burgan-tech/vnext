using System.Text.Json.Serialization;

namespace BBT.Workflow.Runtime;

/// <summary>
/// Represents domain registration information stored in the distributed cache.
/// This information is populated by the domain-registration workflow and used
/// for cross-domain service discovery and routing.
/// </summary>
public sealed class DomainRegistryInfo
{
    /// <summary>
    /// Gets or sets the domain name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the base URL of the domain's API.
    /// </summary>
    [JsonPropertyName("_baseUrl")]
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the health check URL for the domain.
    /// </summary>
    [JsonPropertyName("_healthUrl")]
    public string HealthUrl { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the Dapr application ID for the domain's execution service.
    /// Used for cross-domain task invocation routing.
    /// </summary>
    [JsonPropertyName("_appId")]
    public string AppId { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the domain is currently healthy.
    /// </summary>
    [JsonPropertyName("_isHealthy")]
    public bool IsHealthy { get; init; }

    /// <summary>
    /// Gets or sets the last update timestamp.
    /// </summary>
    [JsonPropertyName("_updatedAt")]
    public string UpdatedAt { get; init; } = string.Empty;
}

