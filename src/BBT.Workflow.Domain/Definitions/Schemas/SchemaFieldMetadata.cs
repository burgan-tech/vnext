namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Represents filter/sort metadata for a single field parsed from JSON Schema custom extensions.
/// </summary>
public sealed class SchemaFieldMetadata
{
    /// <summary>
    /// JSON Schema type (string, number, integer, boolean, object, array).
    /// </summary>
    public string Type { get; init; } = "string";

    /// <summary>
    /// Allowed filter operators from x-filterOperators.
    /// Empty means the field is not filterable.
    /// </summary>
    public IReadOnlyList<string> FilterOperators { get; init; } = [];

    /// <summary>
    /// Whether the field supports sorting (from x-sortable).
    /// </summary>
    public bool Sortable { get; init; }

    /// <summary>
    /// Optional display format hint for UI (from x-displayFormat).
    /// </summary>
    public string? DisplayFormat { get; init; }

    /// <summary>
    /// A field is filterable only when x-filterOperators is present and non-empty.
    /// </summary>
    public bool IsFilterable => FilterOperators.Count > 0;
}
