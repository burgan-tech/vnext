using System.Text.Json.Nodes;
using BBT.Workflow.Mcp.Configuration;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Mcp.Auth;

/// <summary>
/// Resolves the client-presented API key — <c>Authorization: Bearer &lt;key&gt;</c> from the inbound
/// HTTP request (Streamable HTTP transport) or <see cref="McpOptions.ClientApiKey"/> (stdio) — and
/// compares it against the configured per-domain key. Registered <b>scoped</b>: on the HTTP transport
/// each MCP tool call runs inside the inbound POST request scope, so <see cref="IHttpContextAccessor"/>
/// exposes the request even though the SDK does not pass <c>HttpContext</c> to tool methods.
/// </summary>
public sealed class DomainAuthorizer(
    IOptions<McpOptions> options,
    IHttpContextAccessor httpContextAccessor) : IDomainAuthorizer
{
    private const string BearerPrefix = "Bearer ";

    private readonly McpOptions _options = options.Value;

    /// <inheritdoc />
    public JsonNode? CheckDomain(string? domain)
    {
        // No keys configured → authorization disabled (local/dev).
        if (_options.DomainApiKeys.Count == 0)
            return null;

        // Non-domain-scoped calls (e.g. get_runtime_config) are not gated.
        if (string.IsNullOrWhiteSpace(domain))
            return null;

        if (!_options.DomainApiKeys.TryGetValue(domain, out var expectedKey))
            return Error(403, $"Domain '{domain}' is not permitted by this MCP server.");

        var presented = ResolvePresentedKey();
        if (string.IsNullOrEmpty(presented) || !FixedEquals(presented, expectedKey))
            return Error(401, $"Missing or invalid API key for domain '{domain}'.");

        return null;
    }

    private string? ResolvePresentedKey()
    {
        // HTTP transport: Authorization: Bearer <key>.
        var http = httpContextAccessor.HttpContext;
        if (http is not null)
        {
            var header = http.Request.Headers.Authorization.ToString();
            if (header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
                return header[BearerPrefix.Length..].Trim();

            return string.IsNullOrWhiteSpace(header) ? null : header.Trim();
        }

        // stdio transport: no HTTP request — use the configured presented key.
        return _options.ClientApiKey;
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
