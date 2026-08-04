using System.Text.Json.Serialization;

namespace BBT.Workflow;

/// <summary>
/// Interface for schema selections that support rule-based schema resolution.
/// The schema counterpart of <see cref="IViewDefinition"/>; named "selection" rather than
/// "definition" because <c>SchemaDefinition</c> is already the <c>sys-schemas</c> component entity.
/// </summary>
public interface ISchemaSelection
{
    /// <summary>
    /// Array of schema entries with optional rules for conditional selection.
    /// Schemas are evaluated in order, and the first matching schema is returned.
    /// </summary>
    IReadOnlyList<SchemaEntry> Schemas { get; }
}

/// <summary>
/// Helper class for deserializing the single-schema format (a wrapped <c>{ "schema": { ... } }</c> object).
/// </summary>
internal sealed class WrappedSchemaFormat
{
    [JsonPropertyName("schema")]
    public Reference? Schema { get; set; }
}

/// <summary>
/// Represents a schema selection with rule-based resolution support.
/// Contains an array of schema entries, each with an optional rule for conditional selection.
/// Supports the single-schema format and the schemas array format interchangeably.
/// </summary>
public sealed class SchemaSelection : ISchemaSelection
{
    /// <summary>
    /// Array of schema entries with optional rules for conditional selection.
    /// Schemas are evaluated in order, and the first matching schema is returned.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("schemas")]
    public IReadOnlyList<SchemaEntry> Schemas { get; private set; } = Array.Empty<SchemaEntry>();

    /// <summary>
    /// Parameterless constructor for EF Core deserialization.
    /// </summary>
    public SchemaSelection()
    {
        Schemas = Array.Empty<SchemaEntry>();
    }

    /// <summary>
    /// Constructor that supports both the single-schema and the schemas array format.
    /// If both formats are provided, the array format takes precedence.
    /// </summary>
    [JsonConstructor]
    private SchemaSelection(
        List<SchemaEntry>? schemas,
        WrappedSchemaFormat? wrappedSchema)
    {
        if (schemas != null && schemas.Count > 0)
        {
            Schemas = schemas.AsReadOnly();
        }
        else if (wrappedSchema?.Schema != null)
        {
            Schemas = new List<SchemaEntry> { SchemaEntry.CreateDefault(wrappedSchema.Schema) }.AsReadOnly();
        }
        else
        {
            Schemas = Array.Empty<SchemaEntry>();
        }
    }

    /// <summary>
    /// Creates a new SchemaSelection with a single default schema entry (no rule).
    /// </summary>
    public static SchemaSelection CreateDefault(Reference schema)
    {
        return new SchemaSelection([SchemaEntry.CreateDefault(schema)], null);
    }

    /// <summary>
    /// Creates a new SchemaSelection with multiple schema entries.
    /// </summary>
    public static SchemaSelection CreateWithSchemas(params SchemaEntry[] entries)
    {
        return new SchemaSelection(entries.ToList(), null);
    }
}
