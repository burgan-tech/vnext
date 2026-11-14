using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace BBT.Workflow.Agents;

/// <summary>
/// MCP transport over the official SDK (streamable HTTP, issue #1105). One short-lived client per call:
/// connect, run the operation, dispose. Results are normalized to JSON for the task mapping and the agent.
/// </summary>
internal sealed class McpClientGateway(ILoggerFactory loggerFactory) : IMcpClientGateway
{
    /// <inheritdoc />
    public async Task<McpCallResult> ExecuteAsync(McpRequest request, CancellationToken cancellationToken)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(request.Endpoint),
            Name = "vnext-mcp",
            ConnectionTimeout = request.Timeout,
            AdditionalHeaders = request.Headers.ToDictionary(kv => kv.Key, kv => kv.Value)
        }, loggerFactory);

        await using var client = await McpClient.CreateAsync(transport, new McpClientOptions(), loggerFactory, cancellationToken);
        var json = McpJsonUtilities.DefaultOptions;

        switch (request.Operation)
        {
            case McpOperations.ListTools:
            {
                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
                var infos = tools.Select(t => new McpToolInfo(
                    t.ProtocolTool.Name,
                    t.ProtocolTool.Description,
                    t.ProtocolTool.InputSchema)).ToList();
                return new McpCallResult(true, null, false, null, infos);
            }

            case McpOperations.CallTool:
            {
                var args = request.Arguments is { ValueKind: JsonValueKind.Object } a
                    ? a.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
                    : new Dictionary<string, object?>();
                var result = await client.CallToolAsync(request.Tool!, args, cancellationToken: cancellationToken);
                var content = result.StructuredContent is { } structured
                    ? JsonSerializer.SerializeToElement(structured, json)
                    : JsonSerializer.SerializeToElement(new
                    {
                        text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text))
                    });
                var isError = result.IsError == true;
                return new McpCallResult(true, content, isError, isError ? Text(result) : null);
            }

            case McpOperations.ListResources:
                return Ok(await client.ListResourcesAsync(cancellationToken: cancellationToken), r => r.ProtocolResource, json);

            case McpOperations.ReadResource:
            {
                var result = await client.ReadResourceAsync(request.Uri!, cancellationToken: cancellationToken);
                return new McpCallResult(true, JsonSerializer.SerializeToElement(result, json), false, null);
            }

            case McpOperations.ListPrompts:
                return Ok(await client.ListPromptsAsync(cancellationToken: cancellationToken), p => p.ProtocolPrompt, json);

            case McpOperations.GetPrompt:
            {
                var args = request.Arguments is { ValueKind: JsonValueKind.Object } a
                    ? a.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
                    : new Dictionary<string, object?>();
                var result = await client.GetPromptAsync(request.Prompt!, args, cancellationToken: cancellationToken);
                return new McpCallResult(true, JsonSerializer.SerializeToElement(result, json), false, null);
            }

            default:
                return McpCallResult.Failure($"MCP operation '{request.Operation}' is not supported.");
        }
    }

    private static McpCallResult Ok<T, TProtocol>(IEnumerable<T> items, Func<T, TProtocol> select, JsonSerializerOptions json) =>
        new(true, JsonSerializer.SerializeToElement(items.Select(select).ToList(), json), false, null);

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
