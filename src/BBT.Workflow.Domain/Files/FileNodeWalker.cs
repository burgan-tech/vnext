using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Workflow.Definitions.Schemas;

namespace BBT.Workflow.Files;

/// <summary>Locates the JSON objects an <see cref="FileStorageField"/> names inside a payload or a data row.</summary>
public static class FileNodeWalker
{
    public static IEnumerable<(JsonObject Node, string Path)> Find(JsonNode? root, FileStorageField field)
        => Find(root, field.Segments, 0, string.Empty);

    public static bool HasContent(JsonObject node) =>
        node["content"] is JsonValue v && v.GetValueKind() == JsonValueKind.String;

    /// <summary>
    /// Read-only probe over an element (no mutable DOM): does any node at a <paramref name="fields"/> path carry a
    /// <c>content</c> member? Same segment semantics as <see cref="Find(JsonNode?, FileStorageField)"/>, <c>[]</c>
    /// included. Any kind counts — a malformed <c>content</c> is still work: the offload rejects it.
    /// </summary>
    public static bool AnyContent(JsonElement root, IReadOnlyList<FileStorageField> fields)
    {
        foreach (var field in fields)
        {
            if (AnyContent(root, field.Segments, 0))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Schema-less probe, for when the master schema could not be loaded: does any object anywhere in
    /// <paramref name="root"/> carry a <c>content</c> or <c>file</c> member? False ⇒ no x-storage path of any schema
    /// could hold work, so the write is unaffected by the missing schema; true ⇒ the paths are unknown and the write is
    /// refused (<c>FileSchemaUnavailable</c>).
    /// </summary>
    public static bool AnyFileShapedNode(JsonElement root)
    {
        // Fast path over the raw UTF-8: a member named content/file appears verbatim as "content" / "file" unless its
        // name is escaped, and an escape always contains a backslash. No backslash and neither literal ⇒ no such member
        // (a match may also be a string VALUE — then the exact walk below decides).
        if (root.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            var raw = System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(root);
            if (raw.IndexOf((byte)'\\') < 0 && raw.IndexOf("\"file\""u8) < 0 && raw.IndexOf("\"content\""u8) < 0)
                return false;
        }
        return AnyFileShapedMember(root);
    }

    private static bool AnyFileShapedMember(JsonElement root)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in root.EnumerateObject())
                {
                    if (property.NameEquals("content") || property.NameEquals("file"))
                        return true;
                    if (AnyFileShapedMember(property.Value))
                        return true;
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in root.EnumerateArray())
                {
                    if (AnyFileShapedMember(item))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Read-only probe: does any node at a <paramref name="fields"/> path carry a <c>content</c> or a <c>file</c> member?
    /// The trusted write paths use it — a <c>content</c> is offloaded and a <c>file</c> handle is validated (GUID file
    /// id, allowed component), so either one is work.
    /// </summary>
    public static bool AnyContentOrFile(JsonElement root, IReadOnlyList<FileStorageField> fields)
    {
        foreach (var field in fields)
        {
            if (AnyMember(root, field.Segments, 0, includeFile: true))
                return true;
        }
        return false;
    }

    private static bool AnyContent(JsonElement node, IReadOnlyList<string> segments, int index)
        => AnyMember(node, segments, index, includeFile: false);

    private static bool AnyMember(JsonElement node, IReadOnlyList<string> segments, int index, bool includeFile)
    {
        if (index == segments.Count)
            return node.ValueKind == JsonValueKind.Object
                   && (node.TryGetProperty("content", out _) || (includeFile && node.TryGetProperty("file", out _)));

        var segment = segments[index];
        if (segment == "[]")
        {
            if (node.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var item in node.EnumerateArray())
            {
                if (AnyMember(item, segments, index + 1, includeFile))
                    return true;
            }
            return false;
        }

        return node.ValueKind == JsonValueKind.Object
               && node.TryGetProperty(segment, out var child)
               && AnyMember(child, segments, index + 1, includeFile);
    }

    private static IEnumerable<(JsonObject, string)> Find(JsonNode? node, IReadOnlyList<string> segments, int index, string path)
    {
        if (node is null)
            yield break;

        if (index == segments.Count)
        {
            if (node is JsonObject obj)
                yield return (obj, path);
            yield break;
        }

        var segment = segments[index];
        if (segment == "[]")
        {
            if (node is not JsonArray array)
                yield break;
            for (var i = 0; i < array.Count; i++)
                foreach (var hit in Find(array[i], segments, index + 1, $"{path}[{i}]"))
                    yield return hit;
            yield break;
        }

        if (node is JsonObject parent && parent.TryGetPropertyValue(segment, out var child))
            foreach (var hit in Find(child, segments, index + 1, path.Length == 0 ? segment : $"{path}.{segment}"))
                yield return hit;
    }
}
