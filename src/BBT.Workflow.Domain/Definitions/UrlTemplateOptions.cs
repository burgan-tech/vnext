namespace BBT.Workflow.Definitions;

/// <summary>
/// Configuration for client-facing URL templates used in HATEOAS responses.
/// Internal service-to-service URLs remain static in InstanceUrlTemplates as they map to controller routes.
/// <para>
/// <b>One base entry is normally enough.</b> The application already knows its own route shape — it lives
/// in <see cref="UrlTemplateDefaults"/>, mirroring the controller routes. What differs per deployment is
/// only the prefix an API gateway exposes, because an href handed to a client must point at the gateway
/// rather than at the pod. So a host declares <see cref="BasePath"/> and nothing else; omit the section
/// entirely and the application serves its own <c>/api/v1</c> prefix.
/// </para>
/// <para>
/// The nineteen template properties below stay available as <b>optional per-endpoint overrides</b> for
/// the case where a gateway routes one endpoint differently from its siblings. An override is a
/// <b>complete path</b> and is used verbatim — <see cref="BasePath"/> is <i>not</i> prepended to it, so an
/// override must spell out the prefix it wants. That keeps a value authored against the previous
/// all-templates-listed configuration style working unchanged.
/// </para>
/// <para>
/// <b>Consequence when adding a template:</b> add the built-in relative path to
/// <see cref="UrlTemplateDefaults"/> and read it in <c>UrlTemplateBuilder</c>'s constructor. Nothing needs
/// to be added to any host's configuration — a template with no override simply inherits
/// <see cref="BasePath"/>, which is what removed the old failure mode where a forgotten config key
/// silently emitted a prefix-less <c>/{domain}/…</c> href (that is how the function catalog / info / view /
/// schema hrefs once regressed). <c>UrlTemplateConfigCompletenessTests</c> pins that every override
/// property is actually wired into the builder and that every href carries the configured base.
/// </para>
/// </summary>
public sealed class UrlTemplateOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json
    /// </summary>
    public const string SectionName = "UrlTemplates";

    /// <summary>
    /// Prefix prepended to every template that has no explicit override. Defaults to the prefix the
    /// application itself serves (<see cref="UrlTemplateDefaults.BasePath"/>); set it to the API gateway
    /// route when hrefs must point at the gateway (for example <c>/api/v1/monitor</c>). A leading slash is
    /// added and a trailing slash trimmed, so <c>api/v1/</c> and <c>/api/v1</c> are equivalent. An empty
    /// value emits prefix-less paths, for a host mounted at the root.
    /// </summary>
    public string BasePath { get; set; } = UrlTemplateDefaults.BasePath;

    /// <summary>
    /// Override for the instance start endpoint (POST). Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=workflow
    /// </summary>
    public string? Start { get; set; }

    /// <summary>
    /// Override for the instance transition endpoint (PATCH). Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=workflow, {2}=instanceId, {3}=transitionKey
    /// </summary>
    public string? Transition { get; set; }

    /// <summary>
    /// Override for the workflow-scoped function list endpoint (GET). Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=workflow, {2}=function
    /// </summary>
    public string? FunctionList { get; set; }

    /// <summary>
    /// Override for the instance list endpoint (GET). Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=workflow
    /// </summary>
    public string? InstanceList { get; set; }

    /// <summary>
    /// Override for the single instance endpoint (GET). Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance
    /// </summary>
    public string? Instance { get; set; }

    /// <summary>
    /// Override for the instance history/transitions endpoint (GET). Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance
    /// </summary>
    public string? InstanceHistory { get; set; }

    /// <summary>
    /// Override for the instance data endpoint (GET). Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance
    /// </summary>
    public string? Data { get; set; }

    /// <summary>
    /// Override for the instance view endpoint (GET). Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance
    /// </summary>
    public string? View { get; set; }

    /// <summary>
    /// Override for the instance transition schema endpoint (GET). Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=workflow, {2}=instanceId, {3}=transitionKey
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>
    /// Override for the instance master schema endpoint (GET). Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance
    /// </summary>
    public string? Master { get; set; }

    /// <summary>
    /// Override for the domain-scoped function execution endpoint. Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=function
    /// </summary>
    public string? DomainFunction { get; set; }

    /// <summary>
    /// Override for the domain-scoped function info endpoint (GET). Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=function
    /// </summary>
    public string? DomainFunctionInfo { get; set; }

    /// <summary>
    /// Override for the domain-scoped function view endpoint (GET). Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=function, {2}=target (input|output)
    /// </summary>
    public string? DomainFunctionView { get; set; }

    /// <summary>
    /// Override for the domain-scoped function schema endpoint (GET). Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=function, {2}=target (input|output)
    /// </summary>
    public string? DomainFunctionSchema { get; set; }

    /// <summary>
    /// Override for the instance-scoped function execution endpoint. Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance, {3}=function
    /// </summary>
    public string? InstanceFunction { get; set; }

    /// <summary>
    /// Override for the instance-scoped function info endpoint (GET). Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance, {3}=function
    /// </summary>
    public string? InstanceFunctionInfo { get; set; }

    /// <summary>
    /// Override for the instance function catalog endpoint (GET). Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance
    /// </summary>
    public string? FunctionCatalog { get; set; }

    /// <summary>
    /// Override for the instance-scoped function view endpoint (GET). Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance, {3}=function, {4}=target (input|output)
    /// </summary>
    public string? InstanceFunctionView { get; set; }

    /// <summary>
    /// Override for the instance-scoped function schema endpoint (GET). Complete path, used verbatim.
    /// Parameters: {0}=domain, {1}=workflow, {2}=instance, {3}=function, {4}=target (input|output)
    /// </summary>
    public string? InstanceFunctionSchema { get; set; }
}
