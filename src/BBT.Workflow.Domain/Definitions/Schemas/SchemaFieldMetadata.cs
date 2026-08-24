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
    /// Whether the value is stored encrypted (from <c>x-sensitive.encryptAtRest</c>).
    /// <para>
    /// Carried here purely so a rejected filter can say WHY. Filtering runs as raw SQL over the
    /// <c>Data</c> jsonb, so a predicate against ciphertext matches nothing; publish-time
    /// validation already refuses to combine encryption with filter/sort metadata, and this flag
    /// turns the resulting "not filterable" into an explanation instead of a puzzle.
    /// </para>
    /// </summary>
    public bool EncryptedAtRest { get; init; }

    /// <summary>
    /// A field is filterable only when x-filterOperators is present and non-empty.
    /// </summary>
    public bool IsFilterable => FilterOperators.Count > 0;
}
