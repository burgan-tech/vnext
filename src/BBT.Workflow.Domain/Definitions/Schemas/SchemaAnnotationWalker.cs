using System.Text.Json;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// One property node discovered while walking a JSON Schema: its dotted path and the schema
/// object that describes it.
/// </summary>
/// <param name="Path">
/// Dot-separated path from the schema root, with <c>[]</c> marking an array item schema
/// (e.g. <c>amount</c>, <c>nested.field</c>, <c>cards[].number</c>).
/// </param>
/// <param name="Schema">
/// The property's schema object. Borrows the lifetime of the root element passed to
/// <see cref="SchemaAnnotationWalker.Walk"/> — it is not cloned.
/// </param>
public readonly record struct SchemaPropertyNode(string Path, JsonElement Schema);

/// <summary>
/// The single recursive walk over a JSON Schema's property tree, shared by every vocabulary
/// parser that needs "annotation X at path Y" (<c>x-roles</c>, <c>x-filterOperators</c>,
/// <c>x-sensitive</c>, ...). Consolidating the walk is what keeps those parsers agreeing about
/// what a path is; three independently written walks previously disagreed about array items.
/// <para>
/// Traverses <c>properties</c> (dotted segment) and <c>items</c> (<c>[]</c> segment). It does
/// NOT resolve <c>$ref</c> or descend into <c>$defs</c>, <c>definitions</c>, <c>oneOf</c>,
/// <c>anyOf</c>, <c>allOf</c>, <c>if</c>/<c>then</c>/<c>else</c> or <c>patternProperties</c> —
/// an annotation placed under those keywords is unreachable, which
/// <see cref="FindUnreachable"/> reports at definition time rather than letting it be silently
/// inert.
/// </para>
/// </summary>
public static class SchemaAnnotationWalker
{
    private const string PropertiesKey = "properties";
    private const string ItemsKey = "items";

    /// <summary>
    /// Keywords whose values are schemas (or maps/arrays of schemas) that <see cref="Walk"/>
    /// deliberately does not follow. Used by <see cref="FindUnreachable"/> to tell "annotation
    /// somewhere the runtime cannot reach" apart from "no annotation at all".
    /// </summary>
    internal static readonly string[] UnfollowedSchemaKeywords =
    [
        "$defs",
        "definitions",
        "patternProperties",
        "dependentSchemas",
        "oneOf",
        "anyOf",
        "allOf",
        "if",
        "then",
        "else",
        "not",
        "additionalProperties",
        "additionalItems",
        "unevaluatedProperties",
        "unevaluatedItems",
        "propertyNames",
        "contains",
        "prefixItems",
        "contentSchema"
    ];

    /// <summary>
    /// Walks the schema's property tree depth-first in document order, parent before children.
    /// The root itself is not yielded — only named properties and array item schemas.
    /// </summary>
    /// <param name="schemaRoot">Root of the JSON Schema (an object with optional "properties").</param>
    /// <returns>Every reachable property node; empty when the root is not an object.</returns>
    public static IReadOnlyList<SchemaPropertyNode> Walk(JsonElement schemaRoot)
    {
        if (schemaRoot.ValueKind != JsonValueKind.Object)
            return [];

        var nodes = new List<SchemaPropertyNode>();
        WalkNode(schemaRoot, string.Empty, nodes);
        return nodes;
    }

    private static void WalkNode(JsonElement node, string path, List<SchemaPropertyNode> nodes)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;

        if (!string.IsNullOrEmpty(path))
            nodes.Add(new SchemaPropertyNode(path, node));

        if (node.TryGetProperty(PropertiesKey, out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                var childPath = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";
                WalkNode(property.Value, childPath, nodes);
            }
        }

        // Array item schemas carry a "[]" segment so a per-item annotation is addressable.
        // The tuple form (items as an array) and the boolean form are not traversed, matching
        // the single-schema form every vNext master schema uses.
        if (node.TryGetProperty(ItemsKey, out var items) && items.ValueKind == JsonValueKind.Object)
        {
            WalkNode(items, string.IsNullOrEmpty(path) ? "[]" : $"{path}[]", nodes);
        }
    }

    /// <summary>
    /// Collects the paths of schema nodes that sit under a keyword the walk does not follow and
    /// that carry <paramref name="annotationKeyword"/>. Reported as a definition-time warning:
    /// the annotation would otherwise be accepted and then never applied at runtime.
    /// </summary>
    /// <param name="schemaRoot">Root of the JSON Schema.</param>
    /// <param name="annotationKeyword">The vocabulary keyword to look for (e.g. "x-sensitive").</param>
    /// <returns>JSON-pointer-ish locations of unreachable annotations, in document order.</returns>
    public static IReadOnlyList<string> FindUnreachable(JsonElement schemaRoot, string annotationKeyword)
    {
        if (schemaRoot.ValueKind != JsonValueKind.Object)
            return [];

        var unreachable = new List<string>();
        VisitReachable(schemaRoot, string.Empty, annotationKeyword, unreachable);
        return unreachable;
    }

    /// <summary>
    /// Mirrors <see cref="WalkNode"/> exactly, and at every node the walk DOES reach, dives into
    /// the schema-valued keywords it does NOT follow — everything below one of those is
    /// unreachable by construction.
    /// </summary>
    private static void VisitReachable(
        JsonElement node,
        string location,
        string annotationKeyword,
        List<string> unreachable)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;

        foreach (var keyword in UnfollowedSchemaKeywords)
        {
            if (node.TryGetProperty(keyword, out var unfollowed))
                CollectAnnotations(unfollowed, Join(location, keyword), annotationKeyword, unreachable);
        }

        if (node.TryGetProperty(PropertiesKey, out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                VisitReachable(
                    property.Value,
                    Join(Join(location, PropertiesKey), property.Name),
                    annotationKeyword,
                    unreachable);
            }
        }

        if (!node.TryGetProperty(ItemsKey, out var items))
            return;

        // Walk only follows the single-schema form of "items"; the tuple (array) form is not
        // traversed, so anything annotated inside it is unreachable.
        if (items.ValueKind == JsonValueKind.Object)
            VisitReachable(items, Join(location, ItemsKey), annotationKeyword, unreachable);
        else
            CollectAnnotations(items, Join(location, ItemsKey), annotationKeyword, unreachable);
    }

    private static void CollectAnnotations(
        JsonElement node,
        string location,
        string annotationKeyword,
        List<string> unreachable)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                if (node.TryGetProperty(annotationKeyword, out _))
                    unreachable.Add(location);

                foreach (var property in node.EnumerateObject())
                {
                    if (!property.NameEquals(annotationKeyword))
                        CollectAnnotations(property.Value, Join(location, property.Name), annotationKeyword, unreachable);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in node.EnumerateArray())
                {
                    CollectAnnotations(item, $"{location}[{index}]", annotationKeyword, unreachable);
                    index++;
                }

                break;
        }
    }

    private static string Join(string location, string segment)
        => string.IsNullOrEmpty(location) ? segment : $"{location}.{segment}";
}
