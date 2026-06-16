using System.ComponentModel;
using System.Text.Json.Nodes;
using BBT.Workflow.Mcp.Clients;
using BBT.Workflow.Mcp.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace BBT.Workflow.Mcp.Tools;

/// <summary>
/// Read-only MCP tools over existing Orchestration instance/runtime endpoints for the configured domain
/// (<c>Mcp:Domain</c>): instance listing (GraphQL filter, paging, aggregations), single-instance reads,
/// state/data/history/hierarchy, and runtime config.
/// </summary>
[McpServerToolType]
public sealed class RuntimeTools(IOrchestrationClient client, IOptions<McpOptions> options)
{
    private readonly McpOptions _options = options.Value;

    [McpServerTool(Name = "list_instances")]
    [Description("List workflow instances for the configured domain. Supports a GraphQL-style filter envelope (with optional groupBy/aggregations), paging and sort.")]
    public Task<JsonNode?> ListInstancesAsync(
        [Description("The workflow key.")] string workflow,
        [Description("Optional GraphQL-style filter envelope as a JSON string.")] string? filter = null,
        [Description("Optional sort expression, e.g. 'createdAt desc'.")] string? sort = null,
        [Description("1-based page number.")] int page = 1,
        [Description("Page size.")] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var (domain, error) = DomainGuard.Resolve(_options);
        if (error is not null)
            return Task.FromResult<JsonNode?>(error);

        var query = new Dictionary<string, string?>
        {
            ["filter"] = filter,
            ["sort"] = sort,
            ["page"] = page.ToString(),
            ["pageSize"] = pageSize.ToString()
        };

        return client.GetAsync($"{E(domain!)}/workflows/{E(workflow)}/instances", query, cancellationToken);
    }

    [McpServerTool(Name = "get_instance")]
    [Description("Get a single workflow instance by id or key.")]
    public Task<JsonNode?> GetInstanceAsync(string workflow, string instance, CancellationToken cancellationToken = default) =>
        InstancePath(workflow, instance, suffix: null, cancellationToken);

    [McpServerTool(Name = "get_instance_data")]
    [Description("Get the current instance data (data system function).")]
    public Task<JsonNode?> GetInstanceDataAsync(string workflow, string instance, CancellationToken cancellationToken = default) =>
        InstancePath(workflow, instance, "/functions/data", cancellationToken);

    [McpServerTool(Name = "get_instance_state")]
    [Description("Get the current instance state, status and available transitions (state system function).")]
    public Task<JsonNode?> GetInstanceStateAsync(string workflow, string instance, CancellationToken cancellationToken = default) =>
        InstancePath(workflow, instance, "/functions/state", cancellationToken);

    [McpServerTool(Name = "get_instance_history")]
    [Description("Get the transition history of an instance.")]
    public Task<JsonNode?> GetInstanceHistoryAsync(string workflow, string instance, CancellationToken cancellationToken = default) =>
        InstancePath(workflow, instance, "/transitions", cancellationToken);

    [McpServerTool(Name = "get_instance_hierarchy")]
    [Description("Get the subflow/parent hierarchy of an instance (hierarchy system function).")]
    public Task<JsonNode?> GetInstanceHierarchyAsync(string workflow, string instance, CancellationToken cancellationToken = default) =>
        InstancePath(workflow, instance, "/functions/hierarchy", cancellationToken);

    [McpServerTool(Name = "get_runtime_config")]
    [Description("Get the runtime configuration (version, domain, schema map).")]
    public Task<JsonNode?> GetRuntimeConfigAsync(CancellationToken cancellationToken = default) =>
        client.GetAsync("config", query: null, cancellationToken);

    private Task<JsonNode?> InstancePath(string workflow, string instance, string? suffix, CancellationToken cancellationToken)
    {
        var (domain, error) = DomainGuard.Resolve(_options);
        if (error is not null)
            return Task.FromResult<JsonNode?>(error);

        return client.GetAsync(
            $"{E(domain!)}/workflows/{E(workflow)}/instances/{E(instance)}{suffix}",
            query: null,
            cancellationToken);
    }

    private static string E(string segment) => Uri.EscapeDataString(segment);
}
