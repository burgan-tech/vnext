using System.ComponentModel;
using System.Text.Json.Nodes;
using BBT.Workflow.Mcp.Clients;
using ModelContextProtocol.Server;

namespace BBT.Workflow.Mcp.Tools;

/// <summary>
/// MCP tools for discovering and reading vNext runtime component definitions
/// (workflows, tasks, functions, views, extensions, schemas, mappings). Wraps the
/// Orchestration Component Discovery API over HTTP.
/// </summary>
[McpServerToolType]
public sealed class ComponentTools(IOrchestrationClient client)
{
    [McpServerTool(Name = "list_components")]
    [Description("List component summaries across all seven types for a domain, paged. Optionally scope to a single type.")]
    public Task<JsonNode?> ListComponentsAsync(
        [Description("The vNext domain key.")] string domain,
        [Description("Optional component type: workflows, tasks, functions, views, extensions, schemas, mappings.")] string? type = null,
        [Description("1-based page number.")] int page = 1,
        [Description("Page size.")] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(type)
            ? $"{Escape(domain)}/components"
            : $"{Escape(domain)}/components/{Escape(type)}";

        return client.GetAsync(domain, path, Paging(page, pageSize), cancellationToken);
    }

    [McpServerTool(Name = "list_workflows")]
    [Description("List workflow definitions for a domain, paged.")]
    public Task<JsonNode?> ListWorkflowsAsync(string domain, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        ListTypeAsync(domain, "workflows", page, pageSize, cancellationToken);

    [McpServerTool(Name = "list_tasks")]
    [Description("List task definitions for a domain, paged.")]
    public Task<JsonNode?> ListTasksAsync(string domain, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        ListTypeAsync(domain, "tasks", page, pageSize, cancellationToken);

    [McpServerTool(Name = "list_functions")]
    [Description("List function definitions for a domain, paged.")]
    public Task<JsonNode?> ListFunctionsAsync(string domain, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        ListTypeAsync(domain, "functions", page, pageSize, cancellationToken);

    [McpServerTool(Name = "list_views")]
    [Description("List view definitions for a domain, paged.")]
    public Task<JsonNode?> ListViewsAsync(string domain, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        ListTypeAsync(domain, "views", page, pageSize, cancellationToken);

    [McpServerTool(Name = "list_extensions")]
    [Description("List extension definitions for a domain, paged.")]
    public Task<JsonNode?> ListExtensionsAsync(string domain, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        ListTypeAsync(domain, "extensions", page, pageSize, cancellationToken);

    [McpServerTool(Name = "list_schemas")]
    [Description("List schema definitions for a domain, paged.")]
    public Task<JsonNode?> ListSchemasAsync(string domain, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        ListTypeAsync(domain, "schemas", page, pageSize, cancellationToken);

    [McpServerTool(Name = "list_mappings")]
    [Description("List mapping (script-library) definitions for a domain, paged.")]
    public Task<JsonNode?> ListMappingsAsync(string domain, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        ListTypeAsync(domain, "mappings", page, pageSize, cancellationToken);

    [McpServerTool(Name = "get_component")]
    [Description("Get a single component definition by type and key. Pass version 'latest' (default) or a specific SemVer.")]
    public Task<JsonNode?> GetComponentAsync(
        [Description("The vNext domain key.")] string domain,
        [Description("Component type: workflows, tasks, functions, views, extensions, schemas, mappings.")] string type,
        [Description("The component key.")] string key,
        [Description("Component version, or 'latest'.")] string version = "latest",
        CancellationToken cancellationToken = default)
    {
        var path = IsLatest(version)
            ? $"{Escape(domain)}/components/{Escape(type)}/{Escape(key)}"
            : $"{Escape(domain)}/components/{Escape(type)}/{Escape(key)}/{Escape(version)}";

        return client.GetAsync(domain, path, query: null, cancellationToken);
    }

    private Task<JsonNode?> ListTypeAsync(string domain, string type, int page, int pageSize, CancellationToken cancellationToken) =>
        client.GetAsync(domain, $"{Escape(domain)}/components/{type}", Paging(page, pageSize), cancellationToken);

    private static IReadOnlyDictionary<string, string?> Paging(int page, int pageSize) => new Dictionary<string, string?>
    {
        ["page"] = page.ToString(),
        ["pageSize"] = pageSize.ToString()
    };

    private static bool IsLatest(string? version) =>
        string.IsNullOrWhiteSpace(version) || version.Equals("latest", StringComparison.OrdinalIgnoreCase);

    private static string Escape(string segment) => Uri.EscapeDataString(segment);
}

/// <summary>
/// MCP tool for reading decoded mapping <c>.csx</c> source. Registered only when
/// <c>Mcp:AllowCodeRead</c> is enabled.
/// </summary>
[McpServerToolType]
public sealed class MappingCodeTools(IOrchestrationClient client)
{
    [McpServerTool(Name = "get_mapping_code")]
    [Description("Get the decoded .csx source code for a mapping component. Pass version 'latest' (default) or a specific SemVer.")]
    public Task<JsonNode?> GetMappingCodeAsync(
        [Description("The vNext domain key.")] string domain,
        [Description("The mapping component key.")] string key,
        [Description("Mapping version, or 'latest'.")] string version = "latest",
        CancellationToken cancellationToken = default)
    {
        var query = version.Equals("latest", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(version)
            ? null
            : new Dictionary<string, string?> { ["version"] = version };

        return client.GetAsync(
            domain,
            $"{Uri.EscapeDataString(domain)}/components/mappings/{Uri.EscapeDataString(key)}/code",
            query,
            cancellationToken);
    }
}
