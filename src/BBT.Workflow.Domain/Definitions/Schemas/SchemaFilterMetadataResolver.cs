using System.Text.Json;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Parses custom JSON Schema extensions (x-filterOperators, x-sortable, x-displayFormat)
/// from a workflow's master schema into a <see cref="SchemaFilterContext"/>.
/// Follows the same recursive property-walking pattern as <see cref="SchemaRolesParser"/>.
/// </summary>
public static class SchemaFilterMetadataResolver
{
    private const string PropertiesKey = "properties";
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
        if (schemaRoot.ValueKind != JsonValueKind.Object)
            return null;

        var fields = new Dictionary<string, SchemaFieldMetadata>(StringComparer.Ordinal);
        ParsePropertiesRecursive(schemaRoot, string.Empty, fields);

        return fields.Count > 0 ? new SchemaFilterContext(fields) : null;
    }

    private static void ParsePropertiesRecursive(
        JsonElement node,
        string pathPrefix,
        Dictionary<string, SchemaFieldMetadata> result)
    {
        if (node.ValueKind != JsonValueKind.Object)
            return;

        if (!node.TryGetProperty(PropertiesKey, out var properties) || properties.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in properties.EnumerateObject())
        {
            var path = string.IsNullOrEmpty(pathPrefix) ? property.Name : $"{pathPrefix}.{property.Name}";
            var propValue = property.Value;

            if (propValue.ValueKind != JsonValueKind.Object)
                continue;

            var type = ReadStringProperty(propValue, TypeKey) ?? "string";
            var filterOperators = ReadStringArrayProperty(propValue, FilterOperatorsKey);
            var sortable = ReadBooleanProperty(propValue, SortableKey);
            var displayFormat = ReadStringProperty(propValue, DisplayFormatKey);

            result[path] = new SchemaFieldMetadata
            {
                Type = type,
                FilterOperators = filterOperators,
                Sortable = sortable,
                DisplayFormat = displayFormat,
            };

            if (propValue.TryGetProperty(PropertiesKey, out _))
                ParsePropertiesRecursive(propValue, path, result);
        }
    }

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
