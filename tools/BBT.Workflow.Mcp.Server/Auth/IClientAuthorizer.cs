using System.Text.Json.Nodes;

namespace BBT.Workflow.Mcp.Auth;

/// <summary>
/// Authorizes inbound MCP clients against the single <c>Mcp:ApiKey</c> configured for this
/// (single-domain) instance. The MCP server is the auth boundary (the Orchestration runtime has no
/// inbound auth of its own).
/// </summary>
public interface IClientAuthorizer
{
    /// <summary>
    /// Checks whether the current client is authorized. Returns <c>null</c> when authorized; otherwise
    /// a structured error node (<c>{ "error": true, "statusCode": 401, "message": ... }</c>) suitable
    /// for returning directly as the MCP tool result. Open (returns <c>null</c>) when no
    /// <c>Mcp:ApiKey</c> is configured or under the stdio transport (local trust).
    /// </summary>
    JsonNode? Check();
}
