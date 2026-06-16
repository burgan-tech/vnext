using System.Text.Json.Nodes;
using BBT.Workflow.Mcp.Configuration;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Mcp.Auth;

/// <summary>
/// Validates the inbound client's <c>Authorization: Bearer &lt;key&gt;</c> header against the single
/// configured <see cref="McpOptions.ApiKey"/>. Registered <b>scoped</b>: on the HTTP transport each MCP
/// tool call runs inside the inbound POST request scope, so <see cref="IHttpContextAccessor"/> exposes
/// the request even though the SDK does not pass <c>HttpContext</c> to tool methods. The stdio transport
/// (no HTTP request) is treated as locally trusted and is not gated.
/// </summary>
public sealed class ClientAuthorizer(
    IOptions<McpOptions> options,
    IHttpContextAccessor httpContextAccessor) : IClientAuthorizer
{
    private const string BearerPrefix = "Bearer ";

    private readonly McpOptions _options = options.Value;

    /// <inheritdoc />
    public JsonNode? Check()
    {
        // No key configured → authorization disabled (local/dev).
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            return null;

        // stdio transport: no inbound HTTP request; the local user launched the process → trusted.
        var http = httpContextAccessor.HttpContext;
        if (http is null)
            return null;

        var presented = ResolveBearer(http.Request.Headers.Authorization.ToString());
        if (string.IsNullOrEmpty(presented) || !FixedEquals(presented, _options.ApiKey))
            return Error(401, "Missing or invalid API key.");

        return null;
    }

    private static string? ResolveBearer(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return null;

        return header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? header[BearerPrefix.Length..].Trim()
            : header.Trim();
    }

    private static JsonObject Error(int statusCode, string message) => new()
    {
        ["error"] = true,
        ["statusCode"] = statusCode,
        ["message"] = message
    };

    /// <summary>Length-constant string comparison to avoid leaking key length/content via timing.</summary>
    private static bool FixedEquals(string a, string b)
    {
        if (a.Length != b.Length)
            return false;

        var diff = 0;
        for (var i = 0; i < a.Length; i++)
            diff |= a[i] ^ b[i];

        return diff == 0;
    }
}
