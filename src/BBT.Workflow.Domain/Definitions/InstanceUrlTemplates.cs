namespace BBT.Workflow.Definitions;

/// <summary>
/// URL template constants for instance-related API endpoints.
/// These templates follow the RESTful API design pattern and are used for building
/// consistent URL structures across the workflow instance management system.
/// </summary>
public static class InstanceUrlTemplates
{
    /// <summary>
    /// URL template for instance transition endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instanceId}/transitions/{transitionKey}
    /// </summary>
    public const string Transition = "/{0}/workflows/{1}/instances/{2}/transitions/{3}";

    /// <summary>
    /// URL template for instance data endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instanceId}/functions/data
    /// </summary>
    public const string Data = "/{0}/workflows/{1}/instances/{2}/functions/data";

    /// <summary>
    /// URL template for instance data endpoints with extensions.
    /// Format: /{domain}/workflows/{workflow}/instances/{instanceId}/functions/data?extensions={extensions}
    /// </summary>
    public const string DataWithExtensions = "/{0}/workflows/{1}/instances/{2}/functions/data?extensions={3}";

    /// <summary>
    /// URL template for instance view endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instanceId}/functions/view
    /// </summary>
    public const string View = "/{0}/workflows/{1}/instances/{2}/functions/view";

    /// <summary>
    /// URL template for SubFlow instance data endpoints.
    /// Format: /{domain}/workflows/{workflow}/instances/{instanceId}/functions/data
    /// </summary>
    public const string SubFlowData = "/{0}/workflows/{1}/instances/{2}/functions/data";
}

