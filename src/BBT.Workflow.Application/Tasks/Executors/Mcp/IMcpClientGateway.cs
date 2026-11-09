using System.Text.Json;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>One MCP operation to run.</summary>
/// <param name="Endpoint">Server endpoint (already host-checked).</param>
/// <param name="Headers">Resolved request headers (secrets included; never logged).</param>
/// <param name="Operation">One of <see cref="McpOperations"/>.</param>
/// <param name="Tool">Tool name for <c>callTool</c>.</param>
/// <param name="Uri">Resource uri for <c>readResource</c>.</param>
/// <param name="Prompt">Prompt name for <c>getPrompt</c>.</param>
/// <param name="Arguments">Arguments object for <c>callTool</c> and <c>getPrompt</c>.</param>
/// <param name="Timeout">Call timeout.</param>
public sealed record McpRequest(
    string Endpoint,
    IReadOnlyDictionary<string, string> Headers,
    string Operation,
    string? Tool,
    string? Uri,
    string? Prompt,
    JsonElement? Arguments,
    TimeSpan Timeout);

/// <summary>A tool a server lists.</summary>
/// <param name="Name">Tool name.</param>
/// <param name="Description">Tool description.</param>
/// <param name="InputSchema">JSON schema of the arguments.</param>
public sealed record McpToolInfo(string Name, string? Description, JsonElement InputSchema);

/// <summary>Result of one MCP operation.</summary>
/// <param name="IsSuccess">Transport and protocol success.</param>
/// <param name="Content">Normalized result: <c>callTool</c> → structured content or <c>{ text }</c>; list operations → arrays.</param>
/// <param name="IsToolError">The tool ran and reported an error (<c>isError</c>).</param>
/// <param name="Error">Failure text.</param>
/// <param name="Tools">Tools of a <c>listTools</c> call.</param>
public sealed record McpCallResult(
    bool IsSuccess,
    JsonElement? Content,
    bool IsToolError,
    string? Error,
    IReadOnlyList<McpToolInfo>? Tools = null)
{
    /// <summary>A failed call.</summary>
    public static McpCallResult Failure(string error) => new(false, null, false, error);
}

/// <summary>
/// MCP client transport. Implemented by the agents module (ModelContextProtocol SDK, streamable HTTP);
/// hosts without it get <see cref="UnavailableMcpClientGateway"/>.
/// </summary>
public interface IMcpClientGateway
{
    /// <summary>Runs one operation.</summary>
    Task<McpCallResult> ExecuteAsync(McpRequest request, CancellationToken cancellationToken);
}

/// <summary>Gateway used when the agents module is not loaded: every call fails with 503.</summary>
public sealed class UnavailableMcpClientGateway : IMcpClientGateway
{
    /// <inheritdoc />
    public Task<McpCallResult> ExecuteAsync(McpRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(McpCallResult.Failure("MCP client is not available on this host (agents module not loaded)."));
}
