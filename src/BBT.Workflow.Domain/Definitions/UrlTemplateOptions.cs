namespace BBT.Workflow.Definitions;

/// <summary>
/// Configuration for client-facing URL templates used in HATEOAS responses.
/// Internal service-to-service URLs remain static in InstanceUrlTemplates as they map to controller routes.
/// <para>
/// These defaults are the <b>base paths only</b> — deliberately with no gateway prefix. The prefix is a
/// deployment concern the operator declares in the <c>UrlTemplates</c> config section, and it differs
/// per host: the orchestration host serves <c>/api/{domain}/…</c> while the monitor host serves
/// <c>/api/v1/monitor/{domain}/…</c>. Hard-coding either here would be wrong for the other and would
/// put a hosting detail into the domain layer.
/// </para>
/// <para>
/// <b>Consequence when adding a template:</b> add the matching key to every host's <c>UrlTemplates</c>
/// section as well. A template present here but missing from config silently falls back to the
/// prefix-less base path, which is how the function catalog / info / view / schema hrefs came to be
/// emitted as <c>/{domain}/…</c> while every sibling href carried the prefix.
/// <c>UrlTemplateConfigCompletenessTests</c> pins that every property here has a key in each host's
/// configuration, so the omission fails the build instead of reaching a client.
/// </para>
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

    /// <summary>
    /// Template for instance master schema endpoint (GET)
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance
    /// </summary>
    public string Master { get; set; } = "/{0}/workflows/{1}/instances/{2}/functions/master";

    /// <summary>
    /// Template for the domain-scoped function execution endpoint
    /// Parameters: {0}=domain, {1}=function
    /// </summary>
    public string DomainFunction { get; set; } = "/{0}/functions/{1}";

    /// <summary>
    /// Template for the domain-scoped function info endpoint (GET)
    /// Parameters: {0}=domain, {1}=function
    /// </summary>
    public string DomainFunctionInfo { get; set; } = "/{0}/functions/{1}/info";

    /// <summary>
    /// Template for the domain-scoped function view endpoint (GET)
    /// Parameters: {0}=domain, {1}=function, {2}=target (input|output)
    /// </summary>
    public string DomainFunctionView { get; set; } = "/{0}/functions/{1}/view?target={2}";

    /// <summary>
    /// Template for the domain-scoped function schema endpoint (GET)
    /// Parameters: {0}=domain, {1}=function, {2}=target (input|output)
    /// </summary>
    public string DomainFunctionSchema { get; set; } = "/{0}/functions/{1}/schema?target={2}";

    /// <summary>
    /// Template for the instance-scoped function execution endpoint
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance, {3}=function
    /// </summary>
    public string InstanceFunction { get; set; } = "/{0}/workflows/{1}/instances/{2}/functions/{3}";

    /// <summary>
    /// Template for the instance-scoped function info endpoint (GET)
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance, {3}=function
    /// </summary>
    public string InstanceFunctionInfo { get; set; } = "/{0}/workflows/{1}/instances/{2}/functions/{3}/info";

    /// <summary>
    /// Template for the instance function catalog endpoint (GET)
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance
    /// </summary>
    public string FunctionCatalog { get; set; } = "/{0}/workflows/{1}/instances/{2}/functions/catalog";

    /// <summary>
    /// Template for the instance-scoped function view endpoint (GET)
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance, {3}=function, {4}=target (input|output)
    /// </summary>
    public string InstanceFunctionView { get; set; } = "/{0}/workflows/{1}/instances/{2}/functions/{3}/view?target={4}";

    /// <summary>
    /// Template for the instance-scoped function schema endpoint (GET)
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance, {3}=function, {4}=target (input|output)
    /// </summary>
    public string InstanceFunctionSchema { get; set; } = "/{0}/workflows/{1}/instances/{2}/functions/{3}/schema?target={4}";
}
