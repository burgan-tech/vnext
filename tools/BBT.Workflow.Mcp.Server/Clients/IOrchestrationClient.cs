using System.Text.Json.Nodes;

namespace BBT.Workflow.Mcp.Clients;

/// <summary>
/// Thin typed gateway to the Orchestration HTTP API. All methods return a parsed
/// <see cref="JsonNode"/>; non-success responses are surfaced as a structured error
/// object (<c>{ "error": true, "statusCode": n, ... }</c>) rather than throwing, so
/// MCP tool results stay agent-friendly.
/// </summary>
public interface IOrchestrationClient
{
    /// <summary>
    /// Issues a GET to a relative API path with optional query parameters. <paramref name="domain"/>
    /// drives per-call authorization (pass <c>null</c> for non-domain-scoped calls).
    /// </summary>
    Task<JsonNode?> GetAsync(
        string? domain,
        string relativePath,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a request with a JSON body (used by mutating tools). <paramref name="domain"/> drives
    /// per-call authorization (pass <c>null</c> for non-domain-scoped calls).
    /// </summary>
    Task<JsonNode?> SendAsync(
        string? domain,
        HttpMethod method,
        string relativePath,
        JsonNode? body = null,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken cancellationToken = default);
}
