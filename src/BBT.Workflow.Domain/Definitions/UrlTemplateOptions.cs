namespace BBT.Workflow.Definitions;

/// <summary>
/// Configuration for client-facing URL templates used in HATEOAS responses.
/// Internal service-to-service URLs remain static in InstanceUrlTemplates as they map to controller routes.
/// </summary>
public sealed class UrlTemplateOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json
    /// </summary>
    public const string SectionName = "UrlTemplates";
    
    /// <summary>
    /// Template for instance start endpoint (POST)
    /// Parameters: {0}=domain, {1}=workflow
    /// </summary>
    public string Start { get; set; } = "/{0}/workflows/{1}/instances/start";
    
    /// <summary>
    /// Template for instance transition endpoint (PATCH)
    /// Parameters: {0}=domain, {1}=workflow, {2}=instanceId, {3}=transitionKey
    /// </summary>
    public string Transition { get; set; } = "/{0}/workflows/{1}/instances/{2}/transitions/{3}";
    
    /// <summary>
    /// Template for function list endpoint (GET)
    /// Parameters: {0}=domain, {1}=workflow, {2}=function
    /// </summary>
    public string FunctionList { get; set; } = "/{0}/workflows/{1}/functions/{2}";
    
    /// <summary>
    /// Template for instance list endpoint (GET)
    /// Parameters: {0}=domain, {1}=workflow
    /// </summary>
    public string InstanceList { get; set; } = "/{0}/workflows/{1}/instances";
    
    /// <summary>
    /// Template for single instance endpoint (GET)
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance
    /// </summary>
    public string Instance { get; set; } = "/{0}/workflows/{1}/instances/{2}";
    
    /// <summary>
    /// Template for instance history/transitions endpoint (GET)
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance
    /// </summary>
    public string InstanceHistory { get; set; } = "/{0}/workflows/{1}/instances/{2}/transitions";
    
    /// <summary>
    /// Template for instance data endpoint (GET)
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance
    /// </summary>
    public string Data { get; set; } = "/{0}/workflows/{1}/instances/{2}/functions/data";
    
    /// <summary>
    /// Template for instance view endpoint (GET)
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance
    /// </summary>
    public string View { get; set; } = "/{0}/workflows/{1}/instances/{2}/functions/view";
    
    /// <summary>
    /// Template for instance schema endpoint (GET)
    /// Parameters: {0}=domain, {1}=workflow, {2}=instanceId, {3}=transitionKey
    /// </summary>
    public string Schema { get; set; } = "/{0}/workflows/{1}/instances/{2}/functions/schema?transitionKey={3}";
}
