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

    /// <summary>
    /// The object key part the runtime generates: a GUID in <c>D</c> format. Anything else was not written by this
    /// runtime and is never used as a key (no path segments, no traversal, no other object of the bucket).
    /// </summary>
    public static bool IsFileId(string? file) => Guid.TryParseExact(file, "D", out _);

    /// <summary>
    /// A handle the runtime may act on: <see cref="File"/> is a <see cref="IsFileId">file id</see> and
    /// <see cref="Component"/> is one of <paramref name="allowedComponents"/> (the flow's declared <c>x-storage</c>
    /// bindings plus the deployment's <c>FileStorage:AllowedBindings</c>). Never trusted from the stored record alone.
    /// </summary>
    public bool IsAllowed(IReadOnlySet<string> allowedComponents)
        => IsFileId(File) && allowedComponents.Contains(Component);

    public static FileHandle? TryRead(JsonObject node)
    {
        if (node["component"] is not JsonValue c || !c.TryGetValue<string>(out _) ||
            node["file"] is not JsonValue f || !f.TryGetValue<string>(out _))
            return null;
        try
        {
            var handle = node.Deserialize<FileHandle>(Options);
            return handle is not null
                   && !string.IsNullOrWhiteSpace(handle.ETag)
                   && handle.Owner is not null
                   && !string.IsNullOrWhiteSpace(handle.Owner.Domain)
                   && !string.IsNullOrWhiteSpace(handle.Owner.Flow)
                   && !string.IsNullOrWhiteSpace(handle.Owner.Instance)
                ? handle
                : null;
        }
        catch (JsonException) { return null; }
    }
}
