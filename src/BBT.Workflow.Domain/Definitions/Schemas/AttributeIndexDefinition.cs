using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>A versioned physical projection; names never come from caller-supplied SQL.</summary>
public sealed record AttributeIndexDefinition(string Path, string StorageType)
{
    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"v1:latest:{Path}:{StorageType}"))).ToLowerInvariant()[..24];
    public string ColumnName => $"q_{Key}";

    public static IReadOnlyList<AttributeIndexDefinition> From(SchemaFilterContext context)
        => context.Fields.Where(f => f.Value.Indexed).SelectMany(f =>
        {
            // Keep text as well: IN, sorting and MIN/MAX historically compare the extracted text.
            var types = new List<string> { "text" };
            if (f.Value.Type is "number" or "integer") types.Add("numeric");
            if (f.Value.Type == "string" && f.Value.Format == "date-time") types.Add("timestamptz");
            return types.Select(t => new AttributeIndexDefinition(f.Key, t));
        }).ToArray();

    public static void ValidateSchema(JsonElement root) => Visit(root, "", true);

    private static void Visit(JsonElement node, string path, bool supported)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray()) Visit(child, path, false);
            return;
        }
        if (node.ValueKind != JsonValueKind.Object) return;
        if (node.TryGetProperty("x-indexed", out var indexed))
        {
            if (indexed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException($"Field '{path}': x-indexed must be a boolean.");
            if (indexed.ValueKind == JsonValueKind.True &&
                (!supported || !Regex.IsMatch(path, @"^[a-zA-Z][a-zA-Z0-9_]*(\.[a-zA-Z0-9_]+)*$") ||
                 !node.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                 type.GetString() is not ("string" or "number" or "integer" or "boolean") ||
                 node.TryGetProperty("$ref", out _)))
                throw new ArgumentException($"Field '{path}': x-indexed requires an explicit scalar type under object properties; arrays, references and conditional schemas are not supported.");
        }
        foreach (var property in node.EnumerateObject())
        {
            if (property.Name == "properties" && property.Value.ValueKind == JsonValueKind.Object)
            {
                var objectParent = !node.TryGetProperty("type", out var parentType) ||
                    (parentType.ValueKind == JsonValueKind.String && parentType.GetString() == "object");
                foreach (var child in property.Value.EnumerateObject())
                    Visit(child.Value, path.Length == 0 ? child.Name : $"{path}.{child.Name}", supported && objectParent && !child.Name.Contains('.'));
            }
            else if (property.Name is "items" or "prefixItems" or "$defs" or "definitions" or "allOf" or "anyOf" or "oneOf" or "if" or "then" or "else" or "additionalProperties" or "patternProperties")
            {
                if (property.Name is "$defs" or "definitions" or "patternProperties" && property.Value.ValueKind == JsonValueKind.Object)
                    foreach (var child in property.Value.EnumerateObject()) Visit(child.Value, path, false);
                else Visit(property.Value, path, false);
            }
        }
    }
}

public interface IAttributeIndexCatalog
{
    Task<IReadOnlySet<string>> GetReadyAsync(string schema, CancellationToken cancellationToken = default);
}
