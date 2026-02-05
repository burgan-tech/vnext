namespace BBT.Workflow.Definitions;

/// <summary>
/// URL template constants for instance-related API endpoints.
/// These templates follow the RESTful API design pattern and are used for building
/// consistent URL structures across the workflow instance management system.
/// </summary>
public static class InstanceUrlTemplates
{
    #region Template Constants

    /// <summary>
    /// URL template for instance endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instance}
    /// </summary>
    public const string InstanceTemplate = "/{0}/workflows/{1}/instances/{2}";

    /// <summary>
    /// URL template for instance list endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances
    /// </summary>
    public const string InstanceListTemplate = "/{0}/workflows/{1}/instances";

    /// <summary>
    /// URL template for instance history endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instance}/transitions
    /// </summary>
    public const string InstanceHistoryTemplate = "/{0}/workflows/{1}/instances/{2}/transitions";

    /// <summary>
    /// URL template for instance transition endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instanceId}/transitions/{transitionKey}
    /// </summary>
    public const string TransitionTemplate = "/{0}/workflows/{1}/instances/{2}/transitions/{3}";

    /// <summary>
    /// URL template for instance state endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instanceId}/functions/state
    /// </summary>
    public const string StateTemplate = "/{0}/workflows/{1}/instances/{2}/functions/state";

    /// <summary>
    /// URL template for instance data endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instanceId}/functions/data
    /// </summary>
    public const string DataTemplate = "/{0}/workflows/{1}/instances/{2}/functions/data";

    /// <summary>
    /// URL template for instance data endpoints with extensions (base path only).
    /// Format: /{domain}/workflows/{workflow}/instances/{instanceId}/functions/data
    /// Extensions are added as separate query parameters: ?extensions=ext1&amp;extensions=ext2
    /// </summary>
    public const string DataWithExtensionsTemplate = "/{0}/workflows/{1}/instances/{2}/functions/data";

    /// <summary>
    /// URL template for instance view endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instanceId}/functions/view
    /// </summary>
    public const string ViewTemplate = "/{0}/workflows/{1}/instances/{2}/functions/view";

    /// <summary>
    /// URL template for instance schema endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instanceId}/functions/schema?transitionKey={transitionKey}
    /// </summary>
    public const string SchemaTemplate = "/{0}/workflows/{1}/instances/{2}/functions/schema?transitionKey={3}";

    /// <summary>
    /// URL template for instance extensions endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instanceId}/functions/extensions
    /// </summary>
    public const string ExtensionsTemplate = "/{0}/workflows/{1}/instances/{2}/functions/extensions";

    /// <summary>
    /// URL template for start instance endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/start
    /// </summary>
    public const string StartTemplate = "/{0}/workflows/{1}/instances/start";

    /// <summary>
    /// URL template for start sub instance endpoints.
    /// Format: /{domain}/workflows/{workflow}/sub/instances/start
    /// </summary>
    public const string StartSubTemplate = "/{0}/workflows/{1}/sub/instances/start";

    /// <summary>
    /// URL template for complete instance endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instance}/complete
    /// </summary>
    public const string CompleteTemplate = "/{0}/workflows/{1}/instances/{2}/complete";

    /// <summary>
    /// URL template for subflow state update endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instance}/sub/state
    /// </summary>
    public const string SubFlowStateTemplate = "/{0}/workflows/{1}/instances/{2}/sub/state";

    /// <summary>
    /// URL template for retry instance endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instance}/retry
    /// </summary>
    public const string RetryTemplate = "/{0}/workflows/{1}/instances/{2}/retry";

    /// <summary>
    /// URL template for function list endpoints.
    /// Format: /{domain}/workflows/{workflow}/functions/{function}
    /// </summary>
    public const string FunctionListTemplate = "/{0}/workflows/{1}/functions/{2}";

    #endregion

    #region Helper Methods

    /// <summary>
    /// Generates URL for instance endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string Instance(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(InstanceTemplate, apiVersionPrefix, domain, workflow, instance);

    /// <summary>
    /// Generates URL for instance list endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string InstanceList(string domain, string workflow, string? apiVersionPrefix = null)
        => BuildUrl(InstanceListTemplate, apiVersionPrefix, domain, workflow);

    /// <summary>
    /// Generates URL for instance history endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string InstanceHistory(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(InstanceHistoryTemplate, apiVersionPrefix, domain, workflow, instance);

    /// <summary>
    /// Generates URL for instance transition endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instanceId">The instance ID</param>
    /// <param name="transitionKey">The transition key</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string Transition(string domain, string workflow, string instanceId, string transitionKey, string? apiVersionPrefix = null)
        => BuildUrl(TransitionTemplate, apiVersionPrefix, domain, workflow, instanceId, transitionKey);

    /// <summary>
    /// Generates URL for instance state function endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string State(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(StateTemplate, apiVersionPrefix, domain, workflow, instance);

    /// <summary>
    /// Generates URL for instance data function endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string Data(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(DataTemplate, apiVersionPrefix, domain, workflow, instance);

    /// <summary>
    /// Generates URL for instance data function endpoint with extensions.
    /// Each extension is added as a separate query parameter: ?extensions=ext1&amp;extensions=ext2
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="extensions">The collection of extension names</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL with each extension as a separate query parameter</returns>
    public static string DataWithExtensions(string domain, string workflow, string instance, IEnumerable<string> extensions, string? apiVersionPrefix = null)
    {
        var basePath = BuildUrl(DataWithExtensionsTemplate, apiVersionPrefix, domain, workflow, instance);
        var extensionParams = string.Join("&", extensions.Where(e => !string.IsNullOrEmpty(e)).Select(e => $"extensions={Uri.EscapeDataString(e)}"));
        return string.IsNullOrEmpty(extensionParams) ? basePath : $"{basePath}?{extensionParams}";
    }

    /// <summary>
    /// Generates URL for instance view function endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string View(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(ViewTemplate, apiVersionPrefix, domain, workflow, instance);

    /// <summary>
    /// Generates URL for instance schema function endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instanceId">The instance ID</param>
    /// <param name="transitionKey">The transition key</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string Schema(string domain, string workflow, string instanceId, string transitionKey, string? apiVersionPrefix = null)
        => BuildUrl(SchemaTemplate, apiVersionPrefix, domain, workflow, instanceId, transitionKey);

    /// <summary>
    /// Generates URL for instance extensions function endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instanceId">The instance ID</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string Extensions(string domain, string workflow, string instanceId, string? apiVersionPrefix = null)
        => BuildUrl(ExtensionsTemplate, apiVersionPrefix, domain, workflow, instanceId);

    /// <summary>
    /// Generates URL for start instance endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string Start(string domain, string workflow, string? apiVersionPrefix = null)
        => BuildUrl(StartTemplate, apiVersionPrefix, domain, workflow);

    /// <summary>
    /// Generates URL for start sub instance endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string StartSub(string domain, string workflow, string? apiVersionPrefix = null)
        => BuildUrl(StartSubTemplate, apiVersionPrefix, domain, workflow);

    /// <summary>
    /// Generates URL for complete instance endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string Complete(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(CompleteTemplate, apiVersionPrefix, domain, workflow, instance);

    /// <summary>
    /// Generates URL for subflow state update endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string SubFlowState(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(SubFlowStateTemplate, apiVersionPrefix, domain, workflow, instance);

    /// <summary>
    /// Generates URL for retry instance endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string Retry(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(RetryTemplate, apiVersionPrefix, domain, workflow, instance);

    /// <summary>
    /// Generates URL for function list endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="function">The function name</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string FunctionList(string domain, string workflow, string function, string? apiVersionPrefix = null)
        => BuildUrl(FunctionListTemplate, apiVersionPrefix, domain, workflow, function);

    /// <summary>
    /// Generates API version prefix string.
    /// </summary>
    /// <param name="apiVersion">The API version (e.g., "1.0", "1")</param>
    /// <returns>API version prefix (e.g., "api/v1.0")</returns>
    public static string GetApiVersionPrefix(string apiVersion) => $"api/v{apiVersion}";

    /// <summary>
    /// Builds URL by combining optional prefix with formatted template.
    /// </summary>
    private static string BuildUrl(string template, string? apiVersionPrefix, params object[] args)
    {
        var formattedPath = string.Format(template, args);
        return string.IsNullOrEmpty(apiVersionPrefix)
            ? formattedPath
            : apiVersionPrefix + formattedPath;
    }

    #endregion
}
