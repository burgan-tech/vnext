using System.Text.Json.Nodes;

namespace BBT.Workflow.Mcp.Auth;

/// <summary>
/// Authorizes inbound MCP clients against the per-domain fixed keys in <c>Mcp:DomainApiKeys</c>.
/// The MCP server is the auth boundary (the Orchestration runtime has no inbound auth).
/// </summary>
public interface IDomainAuthorizer
{
    /// <summary>
    /// Checks whether the current client is authorized to act on <paramref name="domain"/>.
    /// Returns <c>null</c> when authorized; otherwise a structured error node
    /// (<c>{ "error": true, "statusCode": 401|403, "message": ... }</c>) suitable for returning
    /// directly as the MCP tool result. Open (returns null) when no keys are configured or when
    /// <paramref name="domain"/> is null (non-domain-scoped call).
    /// </summary>
    JsonNode? CheckDomain(string? domain);
}
