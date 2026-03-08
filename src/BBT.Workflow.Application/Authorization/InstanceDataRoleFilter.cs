using System.Text.Json;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Filters instance data (JsonElement) by removing properties that are not visible to the caller
/// according to schema field-level role grants. Paths without "roles" in schema are kept; paths with
/// roles are kept only if in the visible set.
/// </summary>
public static class InstanceDataRoleFilter
{
    /// <summary>
    /// Filters the instance data to only include properties visible to the caller.
    /// </summary>
    /// <param name="data">Root JsonElement (object or array).</param>
    /// <param name="pathsWithRoles">Set of property paths that have role restrictions in the schema (keys from SchemaRolesParser).</param>
    /// <param name="visiblePaths">Set of paths that the caller is allowed to see (from SchemaFieldVisibilityService).</param>
    /// <returns>A new JsonElement containing only visible properties; or the original if nothing to filter.</returns>
    public static JsonElement FilterByVisiblePaths(
        JsonElement data,
        IReadOnlySet<string> pathsWithRoles,
        IReadOnlySet<string> visiblePaths)
    {
        if (pathsWithRoles.Count == 0)
            return data;

        if (data.ValueKind == JsonValueKind.Object)
            return FilterObject(data, string.Empty, pathsWithRoles, visiblePaths);

        return data;
    }

    private static JsonElement FilterObject(
        JsonElement obj,
        string parentPath,
        IReadOnlySet<string> pathsWithRoles,
        IReadOnlySet<string> visiblePaths)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            foreach (var property in obj.EnumerateObject())
            {
                var path = string.IsNullOrEmpty(parentPath) ? property.Name : $"{parentPath}.{property.Name}";
                if (!IsPathVisible(path, pathsWithRoles, visiblePaths))
                    continue;

                writer.WritePropertyName(property.Name);
                WriteFilteredValue(property.Value, path, pathsWithRoles, visiblePaths, writer);
            }
            writer.WriteEndObject();
        }
        stream.Position = 0;
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }

    private static void WriteFilteredValue(
        JsonElement value,
        string currentPath,
        IReadOnlySet<string> pathsWithRoles,
        IReadOnlySet<string> visiblePaths,
        Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var filtered = FilterObject(value, currentPath, pathsWithRoles, visiblePaths);
                filtered.WriteTo(writer);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                for (var i = 0; i < value.GetArrayLength(); i++)
                {
                    var item = value[i];
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        var filteredObj = FilterObject(item, currentPath, pathsWithRoles, visiblePaths);
                        filteredObj.WriteTo(writer);
                    }
                    else
                    {
                        item.WriteTo(writer);
                    }
                }
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static bool IsPathVisible(string path, IReadOnlySet<string> pathsWithRoles, IReadOnlySet<string> visiblePaths)
    {
        if (!pathsWithRoles.Contains(path))
            return true;
        return visiblePaths.Contains(path);
    }
}
