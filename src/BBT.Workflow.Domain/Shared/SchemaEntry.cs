using System.Text.Json.Serialization;
using BBT.Aether;
using BBT.Workflow.Definitions;

namespace BBT.Workflow;

/// <summary>
/// Represents a single schema entry in a schema definition with optional rule-based selection.
/// Each entry can have a conditional rule that determines when this schema should be selected.
/// The schema counterpart of <see cref="ViewEntry"/>; it carries no <c>extensions</c> or
/// <c>loadData</c> because those describe view rendering, not a payload contract.
/// </summary>
public sealed class SchemaEntry
{
    /// <summary>
    /// Optional rule for conditional schema selection.
    /// If null or not defined, this entry acts as a fallback/default schema.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("rule")]
    public ScriptCode? Rule { get; private set; }

    /// <summary>
    /// Reference to the schema to be applied.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("schema")]
    public Reference Schema { get; private set; } = null!;

    /// <summary>
    /// Parameterless constructor for EF Core deserialization.
    /// </summary>
    public SchemaEntry()
    {
    }

    [JsonConstructor]
    private SchemaEntry(
        ScriptCode? rule,
        Reference schema)
    {
        Rule = rule;
        Schema = Check.NotNull(schema, nameof(Schema));
    }

    /// <summary>
    /// Creates a new SchemaEntry with a rule for conditional selection.
    /// </summary>
    public static SchemaEntry CreateWithRule(Reference schema, ScriptCode rule)
    {
        return new SchemaEntry(rule, schema);
    }

    /// <summary>
    /// Creates a new SchemaEntry without a rule (fallback/default schema).
    /// </summary>
    public static SchemaEntry CreateDefault(Reference schema)
    {
        return new SchemaEntry(null, schema);
    }
}
