using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Files;

/// <summary>Which instance's write created a file object; tells a client whose <c>functions/file</c> to call.</summary>
public sealed record FileOwner(
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("flow")] string Flow,
    [property: JsonPropertyName("instance")] string Instance);

/// <summary>The persisted form of an <c>x-storage</c> field. Bytes live in <see cref="Component"/> under <see cref="File"/>.</summary>
public sealed record FileHandle(
    [property: JsonPropertyName("component")] string Component,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("mimeType")] string? MimeType,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("eTag")] string ETag,
    [property: JsonPropertyName("owner")] FileOwner Owner)
{
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public JsonNode ToJsonNode() => JsonSerializer.SerializeToNode(this, Options)!;

    public static FileHandle? TryRead(JsonObject node)
    {
        if (node["component"] is not JsonValue c || !c.TryGetValue<string>(out _) ||
            node["file"] is not JsonValue f || !f.TryGetValue<string>(out _))
            return null;
        try
        {
            var handle = node.Deserialize<FileHandle>(Options);
            return handle is { ETag: not null, Owner: { Domain: not null, Flow: not null, Instance: not null } }
                ? handle
                : null;
        }
        catch (JsonException) { return null; }
    }
}
