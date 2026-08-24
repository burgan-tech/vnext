using System.Text.Json;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Parses custom JSON Schema extensions (x-filterOperators, x-sortable, x-displayFormat)
/// from a workflow's master schema into a <see cref="SchemaFilterContext"/>.
/// Traversal is delegated to <see cref="SchemaAnnotationWalker"/> so this resolver agrees with
/// every other vocabulary parser about what a property path is.
/// </summary>
public static class SchemaFilterMetadataResolver
{
    private const string TypeKey = "type";
    private const string FilterOperatorsKey = "x-filterOperators";
    private const string SortableKey = "x-sortable";
    private const string DisplayFormatKey = "x-displayFormat";

    /// <summary>
    /// Resolves filter/sort metadata from a JSON Schema root element.
    /// Returns null if the schema is not a valid object or has no properties.
    /// </summary>
    public static SchemaFilterContext? Resolve(JsonElement schemaRoot)
    {
        var fields = new Dictionary<string, SchemaFieldMetadata>(StringComparer.Ordinal);

        foreach (var node in SchemaAnnotationWalker.Walk(schemaRoot))
        {
            fields[node.Path] = new SchemaFieldMetadata
            {
                Type = ReadStringProperty(node.Schema, TypeKey) ?? "string",
                FilterOperators = ReadStringArrayProperty(node.Schema, FilterOperatorsKey),
                Sortable = ReadBooleanProperty(node.Schema, SortableKey),
                DisplayFormat = ReadStringProperty(node.Schema, DisplayFormatKey),
                EncryptedAtRest = ReadEncryptAtRest(node.Schema),
            };
        }

        return fields.Count > 0 ? new SchemaFilterContext(fields) : null;
    }

    /// <summary>
    /// Reads <c>x-sensitive.encryptAtRest</c>. Same walk, so the filter metadata and the encryption
    /// metadata can never disagree about which path they describe.
    /// </summary>
    private static bool ReadEncryptAtRest(JsonElement schema)
        => schema.TryGetProperty(SensitiveSchemaParser.SensitiveKey, out var sensitive) &&
           sensitive.ValueKind == JsonValueKind.Object &&
           ReadBooleanProperty(sensitive, "enabled") &&
           ReadBooleanProperty(sensitive, "encryptAtRest");

    private static string? ReadStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool ReadBooleanProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => false
        };
    }

    private static IReadOnlyList<string> ReadStringArrayProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var str = item.GetString();
                if (!string.IsNullOrWhiteSpace(str))
                    list.Add(str);
            }
        }

        return list;
    }
}
