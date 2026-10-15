using System.Text.Json;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Publish-time rules for <c>x-filterOperators</c>. The list query matches a schema DECLARATION against the canonical
/// names (<see cref="Canonical"/>) after mapping its own token (<c>ge</c> → <c>gte</c>, <c>like</c> → <c>contains</c>, …;
/// <see cref="SchemaFilterContext"/>). A declaration spelled as a query token, or not an operator at all, used to publish
/// and then refuse — or never match — every filter on the field; it is refused here instead, naming what to declare.
/// <para>Every placement is checked (nested <c>properties</c>, <c>items</c>, <c>$defs</c>, combinators): the keyword is
/// read only on master-schema fields, but a wrong value is wrong wherever it is written.</para>
/// </summary>
public static class FilterOperatorDefinition
{
    /// <summary>The keyword this definition validates.</summary>
    public const string Keyword = "x-filterOperators";

    /// <summary>The operator names a schema may declare (exact spelling, the vnext-schema vocabulary enum).</summary>
    public static readonly IReadOnlySet<string> Canonical = new HashSet<string>(StringComparer.Ordinal)
    {
        "eq", "neq", "gt", "gte", "lt", "lte", "between", "contains", "startsWith", "endsWith", "in", "nin", "isNull", "includes"
    };

    private static readonly string CanonicalList = string.Join(", ", Canonical);

    /// <summary>Validates every <c>x-filterOperators</c> declaration in <paramref name="root"/>.</summary>
    /// <param name="root">The schema document (<c>attributes.schema</c>).</param>
    /// <returns>One message per violation, each naming the property path; empty when valid.</returns>
    public static IReadOnlyList<string> Validate(JsonElement root)
    {
        var errors = new List<string>();
        Visit(root, string.Empty, errors);
        return errors;
    }

    private static void Visit(JsonElement node, string path, List<string> errors)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray())
                Visit(child, path, errors);
            return;
        }

        if (node.ValueKind != JsonValueKind.Object)
            return;

        if (node.TryGetProperty(Keyword, out var operators))
            ValidateDeclaration(operators, path.Length == 0 ? "(root)" : path, errors);

        foreach (var property in node.EnumerateObject())
        {
            if (property.Name.StartsWith("x-", StringComparison.Ordinal) ||
                property.Name is "default" or "examples" or "enum" or "const")
                continue;

            if (property.Name == "properties" && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in property.Value.EnumerateObject())
                    Visit(child.Value, path.Length == 0 ? child.Name : $"{path}.{child.Name}", errors);
            }
            else if (property.Name is "$defs" or "definitions" or "patternProperties" or "dependentSchemas" &&
                     property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in property.Value.EnumerateObject())
                    Visit(child.Value, path, errors);
            }
            else if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                Visit(property.Value, path, errors);
            }
        }
    }

    private static void ValidateDeclaration(JsonElement operators, string where, List<string> errors)
    {
        if (operators.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"Field '{where}': {Keyword} must be an array of operator names.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in operators.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                errors.Add($"Field '{where}': {Keyword} must be an array of operator names.");
                continue;
            }

            var name = item.GetString()!;
            if (!seen.Add(name))
            {
                errors.Add($"Field '{where}': {Keyword} lists '{name}' more than once.");
                continue;
            }

            if (Canonical.Contains(name))
                continue;

            // A query token (ge, like, startswith …) or a case variant of a declaration name.
            var declared = SchemaFilterContext.InternalToSchemaOperatorMap.TryGetValue(name, out var mapped)
                ? mapped
                : Canonical.FirstOrDefault(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
            errors.Add(declared is not null
                ? $"Field '{where}': {Keyword} value '{name}' is not a declaration name; declare '{declared}'."
                : $"Field '{where}': {Keyword} value '{name}' is not an operator. Allowed: {CanonicalList}.");
        }
    }
}
