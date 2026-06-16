using System.ComponentModel;
using System.Text.Json.Nodes;
using BBT.Workflow.Mcp.Meta;
using ModelContextProtocol.Server;

namespace BBT.Workflow.Mcp.Tools;

/// <summary>
/// MCP tools that expose the offline <c>vnext-meta</c> metadata (features, versions, known issues,
/// deprecations, security policy, component registry). Backed by <see cref="IMetaProvider"/>, which
/// loads the pinned <c>@burgan-tech/vnext-meta</c> npm package into memory at startup — no filesystem
/// dependency, identical under stdio and Streamable HTTP.
/// </summary>
[McpServerToolType]
public sealed class MetaTools(IMetaProvider meta)
{
    [McpServerTool(Name = "query_features")]
    [Description("Return the engine feature matrix (features.json). Optional case-insensitive substring filter over feature key/name/status.")]
    public JsonNode QueryFeatures([Description("Optional substring filter.")] string? filter = null) =>
        FilterItems("features.json", filter);

    [McpServerTool(Name = "get_version_info")]
    [Description("Return the runtime-to-schema version manifest (version-manifest.json).")]
    public JsonNode GetVersionInfo() => Read("version-manifest.json");

    [McpServerTool(Name = "list_known_issues")]
    [Description("Return known issues (known-issues.json). Optional case-insensitive severity filter.")]
    public JsonNode ListKnownIssues([Description("Optional severity, e.g. 'high'.")] string? severity = null) =>
        FilterItems("known-issues.json", severity, "severity");

    [McpServerTool(Name = "get_deprecations")]
    [Description("Return deprecated fields/features (deprecations.json).")]
    public JsonNode GetDeprecations() => Read("deprecations.json");

    [McpServerTool(Name = "check_security_policy")]
    [Description("Return the enforced security policy (security-policy.json).")]
    public JsonNode CheckSecurityPolicy() => Read("security-policy.json");

    [McpServerTool(Name = "list_meta_components")]
    [Description("Return the component registry catalog (component-registry.json).")]
    public JsonNode ListMetaComponents() => Read("component-registry.json");

    private JsonNode Read(string fileName)
    {
        if (!meta.IsLoaded)
            return NotLoaded();

        var node = meta.Get(fileName);
        return node?.DeepClone() ?? new JsonObject { ["error"] = true, ["message"] = $"Meta file not found: {fileName}" };
    }

    /// <summary>
    /// Filters a meta document's <c>items</c> array by a substring. When <paramref name="field"/> is
    /// supplied the match is restricted to that property; otherwise the whole item text is searched.
    /// Returns the document untouched when there is no filter or no <c>items</c> array.
    /// </summary>
    private JsonNode FilterItems(string fileName, string? filter, string? field = null)
    {
        var node = Read(fileName);
        if (string.IsNullOrWhiteSpace(filter) || node is not JsonObject obj || obj["items"] is not JsonArray items)
            return node;

        var matched = items
            .Where(item => item is not null && Matches(item!, filter, field))
            .Select(item => item!.DeepClone())
            .ToArray();

        return new JsonObject { ["items"] = new JsonArray(matched) };
    }

    private static bool Matches(JsonNode item, string filter, string? field)
    {
        var haystack = field is not null && item is JsonObject o
            ? o[field]?.ToString()
            : item.ToJsonString();

        return haystack is not null && haystack.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject NotLoaded() => new()
    {
        ["error"] = true,
        ["message"] = "vnext-meta is not loaded yet (npm package fetch pending or failed). Retry shortly."
    };
}
