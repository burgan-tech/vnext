namespace BBT.Workflow.ServiceDiscovery;

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
    /// Default is true.
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
    /// Gets or sets the registry flow name to use in the HTTP call path.
    /// This is the workflow flow name for domain registration.
    /// </summary>
    public string RegistryFlow { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether SSL certificate validation should be performed for HTTP requests to the registry.
    /// When set to false, SSL certificate validation is disabled (useful for development with self-signed certificates).
    /// Default is true (SSL validation enabled) for production security.
    /// </summary>
    public bool ValidateSsl { get; set; } = true;
}
