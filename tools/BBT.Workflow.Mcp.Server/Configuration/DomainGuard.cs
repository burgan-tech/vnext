using System.Text.Json.Nodes;

namespace BBT.Workflow.Mcp.Configuration;

/// <summary>
/// Resolves the single configured <see cref="McpOptions.Domain"/> for domain-scoped tools, returning a
/// clear, actionable error node when it is not set (each MCP instance is single-domain, so the domain is
/// configuration — not a tool argument).
/// </summary>
public static class DomainGuard
{
    /// <summary>
    /// Returns the configured domain, or an error node to short-circuit the tool when it is missing.
    /// </summary>
    public static (string? Domain, JsonNode? Error) Resolve(McpOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Domain))
        {
            return (null, new JsonObject
            {
                ["error"] = true,
                ["statusCode"] = 500,
                ["message"] = "Mcp:Domain is not configured. Set the vNext domain this MCP instance serves " +
                              "(e.g. environment variable Mcp__Domain=<domain> or the \"Mcp:Domain\" appsettings key)."
            });
        }

        return (options.Domain, null);
    }
}
