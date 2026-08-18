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
    /// URL template for instance master schema endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instanceId}/functions/master
    /// </summary>
    public const string MasterTemplate = "/{0}/workflows/{1}/instances/{2}/functions/master";

    /// <summary>
    /// URL template for instance authorize function endpoint.
    /// Format: /{domain}/workflows/{workflow}/instances/{instanceId}/functions/authorize
    /// </summary>
    public const string AuthorizeTemplate = "/{0}/workflows/{1}/instances/{2}/functions/authorize";

    /// <summary>
    /// URL template for instance permissions (authorization matrix) function endpoint.
    /// Format: /{domain}/workflows/{workflow}/instances/{instanceId}/functions/permissions
    /// </summary>
    public const string PermissionsTemplate = "/{0}/workflows/{1}/instances/{2}/functions/permissions";

    /// <summary>
    /// URL template for instance hierarchy function endpoint.
    /// Format: /{domain}/workflows/{workflow}/instances/{instanceId}/functions/hierarchy
    /// </summary>
    public const string HierarchyTemplate = "/{0}/workflows/{1}/instances/{2}/functions/hierarchy";

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
    /// URL template for subflow fault propagation endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instance}/sub/fault
    /// </summary>
    public const string SubFlowFaultTemplate = "/{0}/workflows/{1}/instances/{2}/sub/fault";

    /// <summary>
    /// URL template for SubItem cancellation propagation endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instance}/sub/cancel
    /// </summary>
    public const string SubFlowCancelTemplate = "/{0}/workflows/{1}/instances/{2}/sub/cancel";

    /// <summary>
    /// URL template for internal downward child-subflow cancellation endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instance}/child-cancel
    /// </summary>
    public const string ChildCancelTemplate = "/{0}/workflows/{1}/instances/{2}/child-cancel";

    /// <summary>
    /// URL template for SubFlow Busy propagation endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instance}/busy
    /// </summary>
    public const string MarkBusyTemplate = "/{0}/workflows/{1}/instances/{2}/busy";

    /// <summary>
    /// URL template for the long-poll acknowledge endpoint.
    /// Format: /{domain}/workflows/{workflow}/instances/{instance}/longpoll/ack
    /// </summary>
    public const string LongPollAckTemplate = "/{0}/workflows/{1}/instances/{2}/longpoll/ack";

    /// <summary>
    /// Internal-only URL template for releasing an accept-time SubFlow chain reserve.
    /// Format: /{domain}/workflows/{workflow}/instances/{instance}/internal/busy-release
    /// </summary>
    public const string ReleaseBusyTemplate = "/{0}/workflows/{1}/instances/{2}/internal/busy-release";

    /// <summary>
    /// Internal-only URL template for relaying a transition to an active SubFlow. Distinct from the
    /// public transition endpoint because it carries the chain-reserve claim in the request body —
    /// the public endpoint copies caller headers unfiltered, so a claim exposed there would be
    /// forgeable. Protected by network isolation, like the related-data endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instance}/internal/subflow-forward
    /// </summary>
    public const string SubflowForwardTemplate = "/{0}/workflows/{1}/instances/{2}/internal/subflow-forward";

    /// <summary>
    /// Internal related-instance data read template.
    /// {0} = domain, {1} = workflow, {2} = instance
    /// </summary>
    public const string RelatedDataTemplate = "/{0}/workflows/{1}/instances/{2}/internal/related-data";

    /// <summary>
    /// Internal batched related-instance data read template.
    /// {0} = domain, {1} = workflow
    /// </summary>
    public const string RelatedDataBatchTemplate = "/{0}/workflows/{1}/internal/related-data/batch";

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

    /// <summary>
    /// URL template for the domain-scoped function execution endpoint.
    /// Format: /{domain}/functions/{function}
    /// </summary>
    public const string DomainFunctionTemplate = "/{0}/functions/{1}";

    /// <summary>
    /// URL template for the domain-scoped function info endpoint.
    /// Format: /{domain}/functions/{function}/info
    /// </summary>
    public const string DomainFunctionInfoTemplate = "/{0}/functions/{1}/info";

    /// <summary>
    /// URL template for the domain-scoped function view endpoint.
    /// Format: /{domain}/functions/{function}/view?target={target}
    /// </summary>
    public const string DomainFunctionViewTemplate = "/{0}/functions/{1}/view?target={2}";

    /// <summary>
    /// URL template for the domain-scoped function schema endpoint.
    /// Format: /{domain}/functions/{function}/schema?target={target}
    /// </summary>
    public const string DomainFunctionSchemaTemplate = "/{0}/functions/{1}/schema?target={2}";

    /// <summary>
    /// URL template for the instance-scoped function execution endpoint.
    /// Format: /{domain}/workflows/{workflow}/instances/{instance}/functions/{function}
    /// </summary>
    public const string InstanceFunctionTemplate = "/{0}/workflows/{1}/instances/{2}/functions/{3}";

    /// <summary>
    /// URL template for the instance-scoped function info endpoint.
    /// Format: /{domain}/workflows/{workflow}/instances/{instance}/functions/{function}/info
    /// </summary>
    public const string InstanceFunctionInfoTemplate = "/{0}/workflows/{1}/instances/{2}/functions/{3}/info";

    /// <summary>
    /// URL template for the instance function catalog endpoint.
    /// Format: /{domain}/workflows/{workflow}/instances/{instance}/functions/catalog
    /// </summary>
    public const string FunctionCatalogTemplate = "/{0}/workflows/{1}/instances/{2}/functions/catalog";

    /// <summary>
    /// URL template for the instance-scoped function view endpoint.
    /// Format: /{domain}/workflows/{workflow}/instances/{instance}/functions/{function}/view?target={target}
    /// </summary>
    public const string InstanceFunctionViewTemplate = "/{0}/workflows/{1}/instances/{2}/functions/{3}/view?target={4}";

    /// <summary>
    /// URL template for the instance-scoped function schema endpoint.
    /// Format: /{domain}/workflows/{workflow}/instances/{instance}/functions/{function}/schema?target={target}
    /// </summary>
    public const string InstanceFunctionSchemaTemplate = "/{0}/workflows/{1}/instances/{2}/functions/{3}/schema?target={4}";

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
    /// Generates URL for instance master schema function endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instanceId">The instance ID</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string Master(string domain, string workflow, string instanceId, string? apiVersionPrefix = null)
        => BuildUrl(MasterTemplate, apiVersionPrefix, domain, workflow, instanceId);

    /// <summary>
    /// Generates URL for instance authorize function endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string Authorize(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(AuthorizeTemplate, apiVersionPrefix, domain, workflow, instance);

    /// <summary>
    /// Generates URL for instance permissions (authorization matrix) function endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string Permissions(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(PermissionsTemplate, apiVersionPrefix, domain, workflow, instance);

    /// <summary>
    /// Generates URL for instance hierarchy function endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string Hierarchy(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(HierarchyTemplate, apiVersionPrefix, domain, workflow, instance);

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
    /// Generates URL for subflow fault propagation endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance key or ID</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string SubFlowFault(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(SubFlowFaultTemplate, apiVersionPrefix, domain, workflow, instance);

    /// <summary>
    /// Generates URL for SubItem cancellation propagation endpoint.
    /// </summary>
    public static string SubFlowCancel(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(SubFlowCancelTemplate, apiVersionPrefix, domain, workflow, instance);

    /// <summary>
    /// Generates URL for internal downward child-subflow cancellation.
    /// </summary>
    public static string ChildCancel(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(ChildCancelTemplate, apiVersionPrefix, domain, workflow, instance);

    /// <summary>
    /// Generates URL for the internal related-instance data read endpoint.
    /// </summary>
    public static string RelatedData(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(RelatedDataTemplate, apiVersionPrefix, domain, workflow, instance);

    /// <summary>
    /// Generates URL for the internal batched related-instance data read endpoint.
    /// </summary>
    public static string RelatedDataBatch(string domain, string workflow, string? apiVersionPrefix = null)
        => BuildUrl(RelatedDataBatchTemplate, apiVersionPrefix, domain, workflow);

    /// <summary>
    /// Generates URL for SubFlow Busy propagation endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance id</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string MarkBusy(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(MarkBusyTemplate, apiVersionPrefix, domain, workflow, instance);

    /// <summary>
    /// Generates URL for the internal-only chain-reserve release endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance id</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string ReleaseBusy(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(ReleaseBusyTemplate, apiVersionPrefix, domain, workflow, instance);

    /// <summary>
    /// Generates URL for the internal-only SubFlow transition relay endpoint.
    /// </summary>
    /// <param name="domain">The domain name</param>
    /// <param name="workflow">The workflow name</param>
    /// <param name="instance">The instance id</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Generated URL</returns>
    public static string SubflowForward(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(SubflowForwardTemplate, apiVersionPrefix, domain, workflow, instance);

    public static string LongPollAck(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(LongPollAckTemplate, apiVersionPrefix, domain, workflow, instance);

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
    /// Generates URL for the domain-scoped function execution endpoint.
    /// </summary>
    public static string DomainFunction(string domain, string function, string? apiVersionPrefix = null)
        => BuildUrl(DomainFunctionTemplate, apiVersionPrefix, domain, function);

    /// <summary>
    /// Generates URL for the domain-scoped function info endpoint.
    /// </summary>
    public static string DomainFunctionInfo(string domain, string function, string? apiVersionPrefix = null)
        => BuildUrl(DomainFunctionInfoTemplate, apiVersionPrefix, domain, function);

    /// <summary>
    /// Generates URL for the domain-scoped function view endpoint. The target is <c>input</c> or <c>output</c>.
    /// </summary>
    public static string DomainFunctionView(string domain, string function, string target, string? apiVersionPrefix = null)
        => BuildUrl(DomainFunctionViewTemplate, apiVersionPrefix, domain, function, Uri.EscapeDataString(target));

    /// <summary>
    /// Generates URL for the domain-scoped function schema endpoint. The target is <c>input</c> or <c>output</c>.
    /// </summary>
    public static string DomainFunctionSchema(string domain, string function, string target, string? apiVersionPrefix = null)
        => BuildUrl(DomainFunctionSchemaTemplate, apiVersionPrefix, domain, function, Uri.EscapeDataString(target));

    /// <summary>
    /// Generates URL for the instance-scoped function execution endpoint.
    /// </summary>
    public static string InstanceFunction(string domain, string workflow, string instance, string function, string? apiVersionPrefix = null)
        => BuildUrl(InstanceFunctionTemplate, apiVersionPrefix, domain, workflow, instance, function);

    /// <summary>
    /// Generates URL for the instance-scoped function info endpoint.
    /// </summary>
    public static string InstanceFunctionInfo(string domain, string workflow, string instance, string function, string? apiVersionPrefix = null)
        => BuildUrl(InstanceFunctionInfoTemplate, apiVersionPrefix, domain, workflow, instance, function);

    /// <summary>
    /// Generates URL for the instance function catalog endpoint.
    /// </summary>
    public static string FunctionCatalog(string domain, string workflow, string instance, string? apiVersionPrefix = null)
        => BuildUrl(FunctionCatalogTemplate, apiVersionPrefix, domain, workflow, instance);

    /// <summary>
    /// Generates URL for the instance-scoped function view endpoint. The target is <c>input</c> or <c>output</c>.
    /// </summary>
    public static string InstanceFunctionView(string domain, string workflow, string instance, string function, string target, string? apiVersionPrefix = null)
        => BuildUrl(InstanceFunctionViewTemplate, apiVersionPrefix, domain, workflow, instance, function, Uri.EscapeDataString(target));

    /// <summary>
    /// Generates URL for the instance-scoped function schema endpoint. The target is <c>input</c> or <c>output</c>.
    /// </summary>
    public static string InstanceFunctionSchema(string domain, string workflow, string instance, string function, string target, string? apiVersionPrefix = null)
        => BuildUrl(InstanceFunctionSchemaTemplate, apiVersionPrefix, domain, workflow, instance, function, Uri.EscapeDataString(target));

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
