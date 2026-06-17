using System.ComponentModel;
using System.Text.Json.Nodes;
using BBT.Workflow.Mcp.Clients;
using BBT.Workflow.Mcp.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace BBT.Workflow.Mcp.Tools;

/// <summary>
/// Mutating MCP tools that write to the runtime (start instances, run transitions,
/// retry, publish definitions, invalidate cache) for the configured domain (<c>Mcp:Domain</c>).
/// Registered <b>only</b> when <c>Mcp:AllowMutations=true</c> — default-off to prevent accidental
/// writes from CI pipelines or autonomous agents.
/// </summary>
[McpServerToolType]
public sealed class MutatingRuntimeTools(IOrchestrationClient client, IOptions<McpOptions> options)
{
    private readonly McpOptions _options = options.Value;

    [McpServerTool(Name = "start_instance")]
    [Description("Start a new workflow instance. 'body' is the raw JSON start payload (key, tags, data, etc.).")]
    public Task<JsonNode?> StartInstanceAsync(
        [Description("The workflow key.")] string workflow,
        [Description("Raw JSON start payload.")] string body,
        CancellationToken cancellationToken = default)
    {
        var (domain, error) = DomainGuard.Resolve(_options);
        if (error is not null)
            return Task.FromResult<JsonNode?>(error);

        return client.SendAsync(HttpMethod.Post, $"{E(domain!)}/workflows/{E(workflow)}/instances/start", Parse(body), query: null, cancellationToken);
    }

    [McpServerTool(Name = "run_transition")]
    [Description("Run a transition on an existing instance. 'body' is the raw JSON transition payload.")]
    public Task<JsonNode?> RunTransitionAsync(
        [Description("The workflow key.")] string workflow,
        [Description("The instance id or key.")] string instance,
        [Description("The transition key.")] string transitionKey,
        [Description("Raw JSON transition payload.")] string body,
        CancellationToken cancellationToken = default)
    {
        var (domain, error) = DomainGuard.Resolve(_options);
        if (error is not null)
            return Task.FromResult<JsonNode?>(error);

        return client.SendAsync(HttpMethod.Patch, $"{E(domain!)}/workflows/{E(workflow)}/instances/{E(instance)}/transitions/{E(transitionKey)}", Parse(body), query: null, cancellationToken);
    }

    [McpServerTool(Name = "retry_instance")]
    [Description("Retry a faulted instance. 'body' is the optional raw JSON retry payload (pass '{}' if none).")]
    public Task<JsonNode?> RetryInstanceAsync(
        [Description("The workflow key.")] string workflow,
        [Description("The instance id or key.")] string instance,
        [Description("Optional raw JSON retry payload.")] string body = "{}",
        CancellationToken cancellationToken = default)
    {
        var (domain, error) = DomainGuard.Resolve(_options);
        if (error is not null)
            return Task.FromResult<JsonNode?>(error);

        return client.SendAsync(HttpMethod.Post, $"{E(domain!)}/workflows/{E(workflow)}/instances/{E(instance)}/retry", Parse(body), query: null, cancellationToken);
    }

    [McpServerTool(Name = "publish_definitions")]
    [Description("Publish component definitions. 'body' is the raw JSON publish payload.")]
    public Task<JsonNode?> PublishDefinitionsAsync(
        [Description("Raw JSON publish payload.")] string body,
        CancellationToken cancellationToken = default) =>
        client.SendAsync(HttpMethod.Post, "definitions/publish", Parse(body), query: null, cancellationToken);

    [McpServerTool(Name = "invalidate_cache")]
    [Description("Invalidate a cached component (reload from DB into Redis). 'body' is the raw JSON invalidate payload.")]
    public Task<JsonNode?> InvalidateCacheAsync(
        [Description("Raw JSON invalidate payload.")] string body,
        CancellationToken cancellationToken = default) =>
        client.SendAsync(HttpMethod.Post, "utilities/invalidate", Parse(body), query: null, cancellationToken);

    private static JsonNode? Parse(string? body) =>
        string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);

    private static string E(string segment) => Uri.EscapeDataString(segment);
}
