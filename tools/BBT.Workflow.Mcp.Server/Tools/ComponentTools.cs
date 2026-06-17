using System.ComponentModel;
using System.Text.Json.Nodes;
using BBT.Workflow.Mcp.Clients;
using BBT.Workflow.Mcp.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace BBT.Workflow.Mcp.Tools;

/// <summary>
/// MCP tools for discovering and reading vNext runtime component definitions
/// (workflows, tasks, functions, views, extensions, schemas, mappings) for the domain this instance is
/// configured to serve (<c>Mcp:Domain</c>). Wraps the Orchestration Component Discovery API over HTTP.
/// </summary>
[McpServerToolType]
public sealed class ComponentTools(IOrchestrationClient client, IOptions<McpOptions> options)
{
    private readonly McpOptions _options = options.Value;

    [McpServerTool(Name = "list_components")]
    [Description("List component summaries across all seven types for the configured domain, paged. Optionally scope to a single type.")]
    public Task<JsonNode?> ListComponentsAsync(
        [Description("Optional component type: workflows, tasks, functions, views, extensions, schemas, mappings.")] string? type = null,
        [Description("1-based page number.")] int page = 1,
        [Description("Page size.")] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var (domain, error) = DomainGuard.Resolve(_options);
        if (error is not null)
            return Task.FromResult<JsonNode?>(error);

        var path = string.IsNullOrWhiteSpace(type)
            ? $"{Escape(domain!)}/components"
            : $"{Escape(domain!)}/components/{Escape(type)}";

        return client.GetAsync(path, Paging(page, pageSize), cancellationToken);
    }

    [McpServerTool(Name = "list_workflows")]
    [Description("List workflow definitions for the configured domain, paged.")]
    public Task<JsonNode?> ListWorkflowsAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        ListTypeAsync("workflows", page, pageSize, cancellationToken);

    [McpServerTool(Name = "list_tasks")]
    [Description("List task definitions for the configured domain, paged.")]
    public Task<JsonNode?> ListTasksAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        ListTypeAsync("tasks", page, pageSize, cancellationToken);

    [McpServerTool(Name = "list_functions")]
    [Description("List function definitions for the configured domain, paged.")]
    public Task<JsonNode?> ListFunctionsAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        ListTypeAsync("functions", page, pageSize, cancellationToken);

    [McpServerTool(Name = "list_views")]
    [Description("List view definitions for the configured domain, paged.")]
    public Task<JsonNode?> ListViewsAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        ListTypeAsync("views", page, pageSize, cancellationToken);

    [McpServerTool(Name = "list_extensions")]
    [Description("List extension definitions for the configured domain, paged.")]
    public Task<JsonNode?> ListExtensionsAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        ListTypeAsync("extensions", page, pageSize, cancellationToken);

    [McpServerTool(Name = "list_schemas")]
    [Description("List schema definitions for the configured domain, paged.")]
    public Task<JsonNode?> ListSchemasAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        ListTypeAsync("schemas", page, pageSize, cancellationToken);

    [McpServerTool(Name = "list_mappings")]
    [Description("List mapping (script-library) definitions for the configured domain, paged.")]
    public Task<JsonNode?> ListMappingsAsync(int page = 1, int pageSize = 20, CancellationToken cancellationToken = default) =>
        ListTypeAsync("mappings", page, pageSize, cancellationToken);

    [McpServerTool(Name = "get_component")]
    [Description("Get a single component definition by type and key. Pass version 'latest' (default) or a specific SemVer.")]
    public Task<JsonNode?> GetComponentAsync(
        [Description("Component type: workflows, tasks, functions, views, extensions, schemas, mappings.")] string type,
        [Description("The component key.")] string key,
        [Description("Component version, or 'latest'.")] string version = "latest",
        CancellationToken cancellationToken = default)
    {
        var (domain, error) = DomainGuard.Resolve(_options);
        if (error is not null)
            return Task.FromResult<JsonNode?>(error);

        var path = IsLatest(version)
            ? $"{Escape(domain!)}/components/{Escape(type)}/{Escape(key)}"
            : $"{Escape(domain!)}/components/{Escape(type)}/{Escape(key)}/{Escape(version)}";

        return client.GetAsync(path, query: null, cancellationToken);
    }

    private Task<JsonNode?> ListTypeAsync(string type, int page, int pageSize, CancellationToken cancellationToken)
    {
        var (domain, error) = DomainGuard.Resolve(_options);
        if (error is not null)
            return Task.FromResult<JsonNode?>(error);

        return client.GetAsync($"{Escape(domain!)}/components/{type}", Paging(page, pageSize), cancellationToken);
    }

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
/// MCP tool for reading decoded mapping <c>.csx</c> source for the configured domain. Registered only
/// when <c>Mcp:AllowCodeRead</c> is enabled.
/// </summary>
[McpServerToolType]
public sealed class MappingCodeTools(IOrchestrationClient client, IOptions<McpOptions> options)
{
    private readonly McpOptions _options = options.Value;

    [McpServerTool(Name = "get_mapping_code")]
    [Description("Get the decoded .csx source code for a mapping component. Pass version 'latest' (default) or a specific SemVer.")]
    public Task<JsonNode?> GetMappingCodeAsync(
        [Description("The mapping component key.")] string key,
        [Description("Mapping version, or 'latest'.")] string version = "latest",
        CancellationToken cancellationToken = default)
    {
        var (domain, error) = DomainGuard.Resolve(_options);
        if (error is not null)
            return Task.FromResult<JsonNode?>(error);

        var query = version.Equals("latest", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(version)
            ? null
            : new Dictionary<string, string?> { ["version"] = version };

        return client.GetAsync(
            $"{Uri.EscapeDataString(domain!)}/components/mappings/{Uri.EscapeDataString(key)}/code",
            query,
            cancellationToken);
    }
}
