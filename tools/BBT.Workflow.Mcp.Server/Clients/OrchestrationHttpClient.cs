using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using BBT.Workflow.Mcp.Auth;
using Microsoft.AspNetCore.WebUtilities;

namespace BBT.Workflow.Mcp.Clients;

/// <summary>
/// <see cref="IOrchestrationClient"/> implementation over a typed <see cref="HttpClient"/>.
/// The base address and outbound headers are configured at registration time (see
/// <c>McpServerSetup</c>). API version segment <c>api/v1.0</c> is prepended to caller-supplied
/// relative paths. Per-call <b>domain authorization</b> runs here via <see cref="IDomainAuthorizer"/>
/// — the single chokepoint every domain-scoped tool flows through.
/// </summary>
public sealed class OrchestrationHttpClient(HttpClient httpClient, IDomainAuthorizer authorizer) : IOrchestrationClient
{
    private const string ApiPrefix = "api/v1.0/";

    /// <inheritdoc />
    public Task<JsonNode?> GetAsync(
        string? domain,
        string relativePath,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(domain, HttpMethod.Get, relativePath, body: null, query, cancellationToken);

    /// <inheritdoc />
    public async Task<JsonNode?> SendAsync(
        string? domain,
        HttpMethod method,
        string relativePath,
        JsonNode? body = null,
        IReadOnlyDictionary<string, string?>? query = null,
        CancellationToken cancellationToken = default)
    {
        // Authorize the inbound client for this domain before touching Orchestration.
        var authError = authorizer.CheckDomain(domain);
        if (authError is not null)
            return authError;

        var path = BuildPath(relativePath, query);

        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            return ErrorNode(0, $"Request to Orchestration failed: {ex.Message}", path);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var parsed = TryParse(content);

        if (response.IsSuccessStatusCode)
        {
            return parsed ?? new JsonObject { ["statusCode"] = (int)response.StatusCode };
        }

        return ErrorNode((int)response.StatusCode, response.ReasonPhrase ?? "Request failed", path, parsed);
    }

    private static string BuildPath(string relativePath, IReadOnlyDictionary<string, string?>? query)
    {
        var path = ApiPrefix + relativePath.TrimStart('/');
        if (query is null || query.Count == 0)
            return path;

        var filtered = query
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);

        return filtered.Count == 0 ? path : QueryHelpers.AddQueryString(path, filtered!);
    }

    private static JsonNode? TryParse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        try
        {
            return JsonNode.Parse(content);
        }
        catch
        {
            return JsonValue.Create(content);
        }
    }

    private static JsonObject ErrorNode(int statusCode, string message, string path, JsonNode? body = null) => new()
    {
        ["error"] = true,
        ["statusCode"] = statusCode,
        ["message"] = message,
        ["path"] = path,
        ["body"] = body?.DeepClone()
    };
}
