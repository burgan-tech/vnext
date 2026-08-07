using BBT.Workflow.Definitions;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Infrastructure.Definitions;

/// <summary>
/// Builds client-facing URLs from configurable templates.
/// Used by controllers to generate HATEOAS links with optional gateway routing.
/// Lifecycle: Singleton - stateless service using configured templates.
/// <para>
/// Each template is resolved once in the constructor: an operator's per-endpoint override wins verbatim,
/// otherwise the configured <see cref="UrlTemplateOptions.BasePath"/> is prepended to the application's
/// own route shape from <see cref="UrlTemplateDefaults"/>. Options are not monitored for change, matching
/// the singleton lifetime.
/// </para>
/// </summary>
public sealed class UrlTemplateBuilder : IUrlTemplateBuilder
{
    private readonly string _start;
    private readonly string _transition;
    private readonly string _functionList;
    private readonly string _instanceList;
    private readonly string _instance;
    private readonly string _instanceHistory;
    private readonly string _data;
    private readonly string _view;
    private readonly string _schema;
    private readonly string _master;
    private readonly string _domainFunction;
    private readonly string _domainFunctionInfo;
    private readonly string _domainFunctionView;
    private readonly string _domainFunctionSchema;
    private readonly string _instanceFunction;
    private readonly string _instanceFunctionInfo;
    private readonly string _functionCatalog;
    private readonly string _instanceFunctionView;
    private readonly string _instanceFunctionSchema;

    /// <summary>
    /// Initializes a new instance of UrlTemplateBuilder with configured templates.
    /// </summary>
    /// <param name="options">URL template configuration options</param>
    public UrlTemplateBuilder(IOptions<UrlTemplateOptions> options)
    {
        var configured = options.Value;
        var basePath = NormalizeBasePath(configured.BasePath);

        string Resolve(string? @override, string relative)
            => string.IsNullOrWhiteSpace(@override) ? basePath + relative : @override;

        _start = Resolve(configured.Start, UrlTemplateDefaults.Start);
        _transition = Resolve(configured.Transition, UrlTemplateDefaults.Transition);
        _functionList = Resolve(configured.FunctionList, UrlTemplateDefaults.FunctionList);
        _instanceList = Resolve(configured.InstanceList, UrlTemplateDefaults.InstanceList);
        _instance = Resolve(configured.Instance, UrlTemplateDefaults.Instance);
        _instanceHistory = Resolve(configured.InstanceHistory, UrlTemplateDefaults.InstanceHistory);
        _data = Resolve(configured.Data, UrlTemplateDefaults.Data);
        _view = Resolve(configured.View, UrlTemplateDefaults.View);
        _schema = Resolve(configured.Schema, UrlTemplateDefaults.Schema);
        _master = Resolve(configured.Master, UrlTemplateDefaults.Master);
        _domainFunction = Resolve(configured.DomainFunction, UrlTemplateDefaults.DomainFunction);
        _domainFunctionInfo = Resolve(configured.DomainFunctionInfo, UrlTemplateDefaults.DomainFunctionInfo);
        _domainFunctionView = Resolve(configured.DomainFunctionView, UrlTemplateDefaults.DomainFunctionView);
        _domainFunctionSchema = Resolve(configured.DomainFunctionSchema, UrlTemplateDefaults.DomainFunctionSchema);
        _instanceFunction = Resolve(configured.InstanceFunction, UrlTemplateDefaults.InstanceFunction);
        _instanceFunctionInfo = Resolve(configured.InstanceFunctionInfo, UrlTemplateDefaults.InstanceFunctionInfo);
        _functionCatalog = Resolve(configured.FunctionCatalog, UrlTemplateDefaults.FunctionCatalog);
        _instanceFunctionView = Resolve(configured.InstanceFunctionView, UrlTemplateDefaults.InstanceFunctionView);
        _instanceFunctionSchema = Resolve(configured.InstanceFunctionSchema, UrlTemplateDefaults.InstanceFunctionSchema);
    }

    /// <inheritdoc />
    public string BuildStartUrl(string domain, string workflow, string? apiVersionPrefix = null)
    {
        var path = string.Format(_start, domain, workflow);
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <inheritdoc />
    public string BuildTransitionUrl(string domain, string workflow, string instanceId, string transitionKey, string? apiVersionPrefix = null)
    {
        var path = string.Format(_transition, domain, workflow, instanceId, transitionKey);
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <inheritdoc />
    public string BuildFunctionListUrl(string domain, string workflow, string function, string? apiVersionPrefix = null)
    {
        var path = string.Format(_functionList, domain, workflow, function);
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <inheritdoc />
    public string BuildInstanceListUrl(string domain, string workflow, string? apiVersionPrefix = null)
    {
        var path = string.Format(_instanceList, domain, workflow);
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <inheritdoc />
    public string BuildInstanceUrl(string domain, string workflow, string instance, string? apiVersionPrefix = null)
    {
        var path = string.Format(_instance, domain, workflow, instance);
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <inheritdoc />
    public string BuildInstanceHistoryUrl(string domain, string workflow, string instance, string? apiVersionPrefix = null)
    {
        var path = string.Format(_instanceHistory, domain, workflow, instance);
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <inheritdoc />
    public string BuildDataUrl(string domain, string workflow, string instance, string? apiVersionPrefix = null)
    {
        var path = string.Format(_data, domain, workflow, instance);
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <inheritdoc />
    public string BuildDataWithExtensionsUrl(string domain, string workflow, string instance, IEnumerable<string> extensions, string? apiVersionPrefix = null)
    {
        var basePath = BuildDataUrl(domain, workflow, instance, apiVersionPrefix);
        var extensionParams = string.Join("&", extensions.Where(e => !string.IsNullOrEmpty(e)).Select(e => $"extensions={Uri.EscapeDataString(e)}"));
        return string.IsNullOrEmpty(extensionParams) ? basePath : $"{basePath}?{extensionParams}";
    }

    /// <inheritdoc />
    public string BuildViewUrl(string domain, string workflow, string instance, string? transitionKey = null, string? apiVersionPrefix = null)
    {
        var path = string.Format(_view, domain, workflow, instance);
        if (!string.IsNullOrEmpty(transitionKey))
            path += "?transitionKey=" + Uri.EscapeDataString(transitionKey);
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <inheritdoc />
    public string BuildSchemaUrl(string domain, string workflow, string instanceId, string transitionKey, string? apiVersionPrefix = null)
    {
        var path = string.Format(_schema, domain, workflow, instanceId, transitionKey);
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <inheritdoc />
    public string BuildMasterUrl(string domain, string workflow, string instance, string? apiVersionPrefix = null)
    {
        var path = string.Format(_master, domain, workflow, instance);
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <inheritdoc />
    public string BuildDomainFunctionUrl(string domain, string function, string? apiVersionPrefix = null)
    {
        var path = string.Format(_domainFunction, domain, function);
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <inheritdoc />
    public string BuildDomainFunctionInfoUrl(string domain, string function, string? apiVersionPrefix = null)
    {
        var path = string.Format(_domainFunctionInfo, domain, function);
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <inheritdoc />
    public string BuildDomainFunctionViewUrl(string domain, string function, string target, string? apiVersionPrefix = null)
    {
        var path = string.Format(_domainFunctionView, domain, function, Uri.EscapeDataString(target));
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <inheritdoc />
    public string BuildDomainFunctionSchemaUrl(string domain, string function, string target, string? apiVersionPrefix = null)
    {
        var path = string.Format(_domainFunctionSchema, domain, function, Uri.EscapeDataString(target));
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <inheritdoc />
    public string BuildInstanceFunctionUrl(string domain, string workflow, string instance, string function, string? apiVersionPrefix = null)
    {
        var path = string.Format(_instanceFunction, domain, workflow, instance, function);
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <inheritdoc />
    public string BuildInstanceFunctionInfoUrl(string domain, string workflow, string instance, string function, string? apiVersionPrefix = null)
    {
        var path = string.Format(_instanceFunctionInfo, domain, workflow, instance, function);
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <inheritdoc />
    public string BuildFunctionCatalogUrl(string domain, string workflow, string instance, string? apiVersionPrefix = null)
    {
        var path = string.Format(_functionCatalog, domain, workflow, instance);
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <inheritdoc />
    public string BuildInstanceFunctionViewUrl(string domain, string workflow, string instance, string function, string target, string? apiVersionPrefix = null)
    {
        var path = string.Format(_instanceFunctionView, domain, workflow, instance, function, Uri.EscapeDataString(target));
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <inheritdoc />
    public string BuildInstanceFunctionSchemaUrl(string domain, string workflow, string instance, string function, string target, string? apiVersionPrefix = null)
    {
        var path = string.Format(_instanceFunctionSchema, domain, workflow, instance, function, Uri.EscapeDataString(target));
        return BuildUrl(path, apiVersionPrefix);
    }

    /// <summary>
    /// Brings a configured base path to the canonical form the templates expect: a leading slash and no
    /// trailing one, so <c>api/v1/</c> and <c>/api/v1</c> compose identically. An empty or whitespace value
    /// yields no prefix at all, for a host mounted at the root.
    /// </summary>
    /// <param name="basePath">The configured base path</param>
    /// <returns>Normalized prefix, possibly empty</returns>
    private static string NormalizeBasePath(string? basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
            return string.Empty;

        var trimmed = basePath.Trim().TrimEnd('/');

        if (trimmed.Length == 0)
            return string.Empty;

        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    /// <summary>
    /// Combines the relative path with optional API version prefix.
    /// <para>
    /// This is a second prefix applied on top of the one already baked into the template by
    /// <see cref="UrlTemplateOptions.BasePath"/>. No client-facing call site passes it — every HATEOAS
    /// caller leaves it null and takes the prefix from configuration.
    /// </para>
    /// </summary>
    /// <param name="path">The relative path generated from template</param>
    /// <param name="apiVersionPrefix">Optional API version prefix (e.g., "api/v1")</param>
    /// <returns>Final URL path</returns>
    private static string BuildUrl(string path, string? apiVersionPrefix)
    {
        return string.IsNullOrEmpty(apiVersionPrefix)
            ? path
            : apiVersionPrefix + path;
    }
}
