namespace BBT.Workflow.Definitions;

/// <summary>
/// Builds client-facing URLs from configurable templates for HATEOAS responses.
/// Internal service-to-service URLs use static InstanceUrlTemplates.
/// </summary>
public interface IUrlTemplateBuilder
{
    /// <summary>
    /// Builds URL for instance start endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated client-facing URL</returns>
    string BuildStartUrl(string domain, string workflow, string? apiVersionPrefix = null);
    
    /// <summary>
    /// Builds URL for instance transition endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instanceId">The instance ID</param>
    /// <param name="transitionKey">The transition key</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated client-facing URL</returns>
    string BuildTransitionUrl(string domain, string workflow, string instanceId, string transitionKey, string? apiVersionPrefix = null);
    
    /// <summary>
    /// Builds URL for function list endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="function">The function name</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated client-facing URL</returns>
    string BuildFunctionListUrl(string domain, string workflow, string function, string? apiVersionPrefix = null);
    
    /// <summary>
    /// Builds URL for instance list endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated client-facing URL</returns>
    string BuildInstanceListUrl(string domain, string workflow, string? apiVersionPrefix = null);
    
    /// <summary>
    /// Builds URL for single instance endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated client-facing URL</returns>
    string BuildInstanceUrl(string domain, string workflow, string instance, string? apiVersionPrefix = null);
    
    /// <summary>
    /// Builds URL for instance history endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated client-facing URL</returns>
    string BuildInstanceHistoryUrl(string domain, string workflow, string instance, string? apiVersionPrefix = null);
    
    /// <summary>
    /// Builds URL for instance data endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated client-facing URL</returns>
    string BuildDataUrl(string domain, string workflow, string instance, string? apiVersionPrefix = null);
    
    /// <summary>
    /// Builds URL for instance data endpoint with extensions.
    /// Each extension is added as a separate query parameter: ?extensions=ext1&extensions=ext2
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="extensions">The collection of extension names</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated client-facing URL with extensions as query parameters</returns>
    string BuildDataWithExtensionsUrl(string domain, string workflow, string instance, IEnumerable<string> extensions, string? apiVersionPrefix = null);
    
    /// <summary>
    /// Builds URL for instance view endpoint. When transitionKey is provided, appends ?transitionKey= to support transition-specific view requests.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="transitionKey">Optional transition key for transition-specific view URL</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated client-facing URL</returns>
    string BuildViewUrl(string domain, string workflow, string instance, string? transitionKey = null, string? apiVersionPrefix = null);
    
    /// <summary>
    /// Builds URL for instance schema endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instanceId">The instance ID</param>
    /// <param name="transitionKey">The transition key</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated client-facing URL</returns>
    string BuildSchemaUrl(string domain, string workflow, string instanceId, string transitionKey, string? apiVersionPrefix = null);
}
