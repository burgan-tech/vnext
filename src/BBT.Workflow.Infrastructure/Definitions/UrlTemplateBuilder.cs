using BBT.Workflow.Definitions;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Infrastructure.Definitions;

/// <summary>
/// Builds client-facing URLs from configurable templates.
/// Used by controllers to generate HATEOAS links with optional gateway routing.
/// Lifecycle: Singleton - stateless service using configured templates.
/// </summary>
public sealed class UrlTemplateBuilder : IUrlTemplateBuilder
{
    private readonly UrlTemplateOptions _options;
    
    /// <summary>
    /// Initializes a new instance of UrlTemplateBuilder with configured templates.
    /// </summary>
    /// <param name="options">URL template configuration options</param>
    public UrlTemplateBuilder(IOptions<UrlTemplateOptions> options)
    {
        _options = options.Value;
    }
    
    /// <inheritdoc />
    public string BuildStartUrl(string domain, string workflow, string? apiVersionPrefix = null)
    {
        var path = string.Format(_options.Start, domain, workflow);
        return BuildUrl(path, apiVersionPrefix);
    }
    
    /// <inheritdoc />
    public string BuildTransitionUrl(string domain, string workflow, string instanceId, string transitionKey, string? apiVersionPrefix = null)
    {
        var path = string.Format(_options.Transition, domain, workflow, instanceId, transitionKey);
        return BuildUrl(path, apiVersionPrefix);
    }
    
    /// <inheritdoc />
    public string BuildFunctionListUrl(string domain, string workflow, string function, string? apiVersionPrefix = null)
    {
        var path = string.Format(_options.FunctionList, domain, workflow, function);
        return BuildUrl(path, apiVersionPrefix);
    }
    
    /// <inheritdoc />
    public string BuildInstanceListUrl(string domain, string workflow, string? apiVersionPrefix = null)
    {
        var path = string.Format(_options.InstanceList, domain, workflow);
        return BuildUrl(path, apiVersionPrefix);
    }
    
    /// <inheritdoc />
    public string BuildInstanceUrl(string domain, string workflow, string instance, string? apiVersionPrefix = null)
    {
        var path = string.Format(_options.Instance, domain, workflow, instance);
        return BuildUrl(path, apiVersionPrefix);
    }
    
    /// <inheritdoc />
    public string BuildInstanceHistoryUrl(string domain, string workflow, string instance, string? apiVersionPrefix = null)
    {
        var path = string.Format(_options.InstanceHistory, domain, workflow, instance);
        return BuildUrl(path, apiVersionPrefix);
    }
    
    /// <inheritdoc />
    public string BuildDataUrl(string domain, string workflow, string instance, string? apiVersionPrefix = null)
    {
        var path = string.Format(_options.Data, domain, workflow, instance);
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
    public string BuildViewUrl(string domain, string workflow, string instance, string? apiVersionPrefix = null)
    {
        var path = string.Format(_options.View, domain, workflow, instance);
        return BuildUrl(path, apiVersionPrefix);
    }
    
    /// <inheritdoc />
    public string BuildSchemaUrl(string domain, string workflow, string instanceId, string transitionKey, string? apiVersionPrefix = null)
    {
        var path = string.Format(_options.Schema, domain, workflow, instanceId, transitionKey);
        return BuildUrl(path, apiVersionPrefix);
    }
    
    /// <summary>
    /// Combines the relative path with optional API version prefix.
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
