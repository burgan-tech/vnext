using System.Runtime.CompilerServices;
using System.Text.Json;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// One <c>x-computed</c> field the runtime evaluates (<c>evaluatedBy: "runtime"</c>).
/// </summary>
/// <param name="Path">Dot-separated path of the computed property (e.g. <c>customer.fullName</c>).</param>
/// <param name="ParentPath">Path of the object that holds it; empty for a root property.</param>
/// <param name="Name">The property name inside the parent object.</param>
/// <param name="Declaration">The <c>x-computed</c> object (cloned; independent of the schema document).</param>
public sealed record ComputedFieldRule(string Path, string ParentPath, string Name, JsonElement Declaration);

/// <summary>
/// Reads the <c>x-computed</c> fields of a master schema that the runtime evaluates. Only properties reachable through
/// nested <c>properties</c> are considered — the same reachability <c>x-roles</c> and <c>x-masking</c> have, and the only
/// one the publish validator (<see cref="ComputedFieldDefinition"/>) admits. <c>evaluatedBy: "client"</c> (and an absent
/// <c>evaluatedBy</c>) is a client declaration and is not returned.
/// </summary>
public static class ComputedFieldParser
{
    /// <summary>The vocabulary keyword.</summary>
    public const string Keyword = "x-computed";

    /// <summary><c>evaluatedBy</c> value the runtime evaluates.</summary>
    public const string RuntimeEvaluator = "runtime";

    /// <summary><c>evaluatedBy</c> value a client evaluates; the runtime ignores it.</summary>
    public const string ClientEvaluator = "client";

    private static readonly ConditionalWeakTable<SchemaDefinition, IReadOnlyList<ComputedFieldRule>> Memo = new();

    /// <summary>The runtime-evaluated computed fields of <paramref name="schema"/>, parsed once per definition instance.</summary>
    public static IReadOnlyList<ComputedFieldRule> GetRules(SchemaDefinition schema)
        => Memo.GetValue(schema, static s => Parse(s.Schema));

    /// <summary>The runtime-evaluated computed fields of a schema document, in declaration order.</summary>
    public static IReadOnlyList<ComputedFieldRule> Parse(JsonElement schemaRoot)
    {
        var rules = new List<ComputedFieldRule>();
        Collect(schemaRoot, string.Empty, rules);
        return rules;
    }

    /// <summary>True when the declaration asks the runtime to evaluate it (<c>evaluatedBy: "runtime"</c>, exact).</summary>
    public static bool IsRuntimeEvaluated(JsonElement declaration)
        => declaration.ValueKind == JsonValueKind.Object &&
           declaration.TryGetProperty("evaluatedBy", out var by) &&
           by.ValueKind == JsonValueKind.String &&
           by.GetString() == RuntimeEvaluator;

    private static void Collect(JsonElement node, string pathPrefix, List<ComputedFieldRule> rules)
    {
        if (node.ValueKind != JsonValueKind.Object ||
            !node.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in properties.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
                continue;

            var path = pathPrefix.Length == 0 ? property.Name : $"{pathPrefix}.{property.Name}";
            if (property.Value.TryGetProperty(Keyword, out var declaration) && IsRuntimeEvaluated(declaration))
                rules.Add(new ComputedFieldRule(path, pathPrefix, property.Name, declaration.Clone()));

            Collect(property.Value, path, rules);
        }
    }
}
