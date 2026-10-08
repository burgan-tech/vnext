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

    private static bool AnyContent(JsonElement node, IReadOnlyList<string> segments, int index)
    {
        if (index == segments.Count)
            return node.ValueKind == JsonValueKind.Object && node.TryGetProperty("content", out _);

        var segment = segments[index];
        if (segment == "[]")
        {
            if (node.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var item in node.EnumerateArray())
            {
                if (AnyContent(item, segments, index + 1))
                    return true;
            }
            return false;
        }

        return node.ValueKind == JsonValueKind.Object
               && node.TryGetProperty(segment, out var child)
               && AnyContent(child, segments, index + 1);
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
