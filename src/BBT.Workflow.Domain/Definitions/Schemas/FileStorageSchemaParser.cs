using System.Text.Json;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>A master-schema location whose file bytes live in a Dapr binding instead of the record.</summary>
/// <param name="Segments">Property names from the root; <c>"[]"</c> means every element of the array.</param>
/// <param name="Binding">The Dapr binding component name (<c>x-storage.binding</c>).</param>
public sealed record FileStorageField(IReadOnlyList<string> Segments, string Binding)
{
    /// <summary>Dot path with array markers folded: <c>files[]</c>, <c>customer.tax</c>.</summary>
    public string Path { get; } = string.Join('.', Segments).Replace(".[]", "[]", StringComparison.Ordinal);
}

/// <summary>
/// Reads and validates <c>x-storage</c> (vnext-client-sdk-core#101). Reachability: nested <c>properties</c>, plus
/// the <c>items</c> schema of an array property reachable that way (one array level). Anything else would be
/// silently ignored by the write path, so it is rejected at publish.
/// </summary>
public static class FileStorageSchemaParser
{
    /// <summary>The master-schema keyword name.</summary>
    public const string Keyword = "x-storage";
    private const string ArraySegment = "[]";

    /// <summary>Returns every valid file node reachable from the schema root; invalid declarations are skipped.</summary>
    public static IReadOnlyList<FileStorageField> Parse(JsonElement schemaRoot)
    {
        var fields = new List<FileStorageField>();
        if (schemaRoot.ValueKind == JsonValueKind.Object)
            Walk(schemaRoot, [], fields, errors: null, insideArray: false);
        return fields;
    }

    /// <summary>Returns publish-time error messages for every misplaced or malformed <c>x-storage</c>; empty when valid.</summary>
    public static IReadOnlyList<string> Validate(JsonElement schemaRoot)
    {
        var errors = new List<string>();
        if (schemaRoot.ValueKind != JsonValueKind.Object)
            return errors;
        if (schemaRoot.TryGetProperty(Keyword, out _))
            errors.Add($"{Keyword} cannot be declared on the schema root; declare it on a property or an array property's items.");
        Walk(schemaRoot, [], fields: null, errors, insideArray: false);
        ReportUnreachable(schemaRoot, reachable: true, errors);
        return errors;
    }

    private static void Walk(JsonElement node, List<string> prefix, List<FileStorageField>? fields,
        List<string>? errors, bool insideArray)
    {
        if (!node.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
                continue;
            var segments = new List<string>(prefix) { property.Name };
            VisitSchema(property.Value, segments, fields, errors, insideArray);
        }
    }

    private static void VisitSchema(JsonElement schema, List<string> segments, List<FileStorageField>? fields,
        List<string>? errors, bool insideArray)
    {
        if (schema.TryGetProperty(Keyword, out var declaration))
        {
            var path = new FileStorageField(segments, string.Empty).Path;
            var binding = ReadBinding(declaration, path, schema, errors);
            if (binding is not null)
                fields?.Add(new FileStorageField(segments, binding));
            if (errors is not null && schema.TryGetProperty("properties", out var inner) && ContainsKeyword(inner))
                errors.Add($"Field '{path}': {Keyword} cannot be declared inside another {Keyword} file node.");
            return; // a file node has no further file nodes beneath it
        }

        if (schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            var itemSegments = new List<string>(segments) { ArraySegment };
            if (insideArray)
            {
                if (ContainsKeyword(items))
                    errors?.Add($"Field '{new FileStorageField(itemSegments, "").Path}': {Keyword} is not supported inside a nested array.");
                return;
            }
            VisitSchema(items, itemSegments, fields, errors, insideArray: true);
            return;
        }

        Walk(schema, segments, fields, errors, insideArray);
    }

    private static string? ReadBinding(JsonElement declaration, string path, JsonElement schema, List<string>? errors)
    {
        if (declaration.ValueKind != JsonValueKind.Object)
        {
            errors?.Add($"Field '{path}': {Keyword} must be an object {{ \"binding\": \"<component>\" }}.");
            return null;
        }

        foreach (var member in declaration.EnumerateObject())
            if (member.Name != "binding")
                errors?.Add($"Field '{path}': {Keyword} has unknown member '{member.Name}'.");

        var binding = declaration.TryGetProperty("binding", out var b) && b.ValueKind == JsonValueKind.String
            ? b.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(binding))
        {
            errors?.Add($"Field '{path}': {Keyword}.binding must be a non-empty component name.");
            return null;
        }

        if (schema.TryGetProperty("type", out var type) &&
            !(type.ValueKind == JsonValueKind.String && type.GetString() == "object") &&
            !(type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == "object")))
            errors?.Add($"Field '{path}': {Keyword} requires type \"object\" (the persisted file handle).");

        if ((schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object &&
             props.TryGetProperty("content", out _)) ||
            (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array &&
             req.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String && r.GetString() == "content")))
            errors?.Add($"Field '{path}': the master schema describes the persisted handle; 'content' is never stored and may not be declared.");

        return binding;
    }

    private static bool ContainsKeyword(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object &&
        (node.TryGetProperty(Keyword, out _) ||
         node.EnumerateObject().Any(p => ContainsKeyword(p.Value)));

    /// <summary>
    /// Any x-storage the reachable walk did not visit (under $defs, combinators, conditionals). Reachable =
    /// each schema inside a <c>properties</c> map and an object <c>items</c>; every other member is not.
    /// </summary>
    private static void ReportUnreachable(JsonElement node, bool reachable, List<string> errors)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray())
                ReportUnreachable(child, reachable: false, errors);
            return;
        }
        if (node.ValueKind != JsonValueKind.Object)
            return;

        foreach (var member in node.EnumerateObject())
        {
            if (member.Name == Keyword)
            {
                if (!reachable)
                    errors.Add($"{Keyword} must be reachable through 'properties' (or one array 'items'); it is ignored under $defs, combinators or conditionals.");
                continue;
            }

            if (member.Name == "properties" && member.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in member.Value.EnumerateObject())
                    ReportUnreachable(property.Value, reachable, errors);
                continue;
            }

            if (member.Name == "items" && member.Value.ValueKind == JsonValueKind.Object)
            {
                ReportUnreachable(member.Value, reachable, errors);
                continue;
            }

            ReportUnreachable(member.Value, reachable: false, errors);
        }
    }
}
