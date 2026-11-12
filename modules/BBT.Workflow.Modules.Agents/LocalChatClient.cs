using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace BBT.Workflow.Agents;

/// <summary>
/// Deterministic in-process provider (<c>kind: local</c>) for local runs and integration tests.
/// First turn: calls every offered tool once, with arguments built from its schema. Next turn: answers
/// with an object built from the response schema (<c>const</c>, then <c>default</c>, then the first
/// <c>enum</c> value, then an empty value of the declared type), so a decision enum resolves to its
/// first candidate. No network, no randomness.
/// </summary>
internal sealed class LocalChatClient(string modelName) : IChatClient
{
    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var lastUser = list.FindLastIndex(m => m.Role == ChatRole.User);
        var toolsAlreadyCalled = list.Skip(lastUser + 1).Any(m => m.Contents.OfType<FunctionResultContent>().Any());
        var functions = options?.Tools?.OfType<AIFunctionDeclaration>().ToList() ?? [];

        var reply = !toolsAlreadyCalled && functions.Count > 0 ? ToolCallReply(functions) : AnswerReply(options);

        var promptChars = list.Sum(m => m.Text?.Length ?? 0);
        return Task.FromResult(new ChatResponse(reply)
        {
            ModelId = modelName,
            ResponseId = $"local-{Guid.NewGuid():N}",
            FinishReason = reply.Contents.OfType<FunctionCallContent>().Any() ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            Usage = new UsageDetails
            {
                InputTokenCount = Math.Max(1, promptChars / 4),
                OutputTokenCount = Math.Max(1, (reply.Text?.Length ?? 0) / 4),
                TotalTokenCount = Math.Max(1, promptChars / 4) + Math.Max(1, (reply.Text?.Length ?? 0) / 4)
            }
        });
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("local", null, modelName) : null;

    /// <inheritdoc />
    public void Dispose()
    {
    }

    /// <summary>Builds a value from a JSON schema.</summary>
    internal static object? Synthesize(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return null;
        if (schema.TryGetProperty("const", out var c))
            return JsonSerializer.Deserialize<object>(c.GetRawText());
        if (schema.TryGetProperty("default", out var d))
            return JsonSerializer.Deserialize<object>(d.GetRawText());
        if (schema.TryGetProperty("enum", out var e) && e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0)
            return JsonSerializer.Deserialize<object>(e[0].GetRawText());

        var type = SchemaType(schema);

        switch (type)
        {
            case "object":
                var obj = new Dictionary<string, object?>();
                if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in props.EnumerateObject())
                        obj[p.Name] = Synthesize(p.Value);
                }

                return obj;
            case "array":
                return new List<object?>();
            case "integer":
            case "number":
                return 0;
            case "boolean":
                return false;
            case "string":
                return schema.TryGetProperty("format", out var f) && f.GetString() == "date-time"
                    ? "2000-01-01T00:00:00Z"
                    : "local";
            default:
                return null;
        }
    }

    /// <summary>The schema's <c>type</c>: the first non-null entry of a type list, <c>object</c> when absent.</summary>
    private static string? SchemaType(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var t))
            return "object";
        return t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => x != "null")
            : t.GetString();
    }

    /// <summary>A reply that calls every offered tool once, with arguments synthesized from its schema.</summary>
    private static ChatMessage ToolCallReply(IReadOnlyList<AIFunctionDeclaration> functions) =>
        new(ChatRole.Assistant, functions.Select((f, i) => (AIContent)new FunctionCallContent(
            $"local_{i + 1}", f.Name, Synthesize(f.JsonSchema) as IDictionary<string, object?> ?? new Dictionary<string, object?>())).ToList());

    /// <summary>A final answer synthesized from the response schema, or an empty object without one.</summary>
    private static ChatMessage AnswerReply(ChatOptions? options)
    {
        var schema = (options?.ResponseFormat as ChatResponseFormatJson)?.Schema;
        return new ChatMessage(ChatRole.Assistant, schema is { } s ? JsonSerializer.Serialize(Synthesize(s)) : "{}");
    }
}
