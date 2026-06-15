namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Holds the full field metadata map parsed from a workflow's JSON Schema and provides
/// validation methods for filter/sort operations.
/// </summary>
public sealed class SchemaFilterContext
{
    private static readonly Dictionary<string, string> InternalToSchemaOperatorMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eq"] = "eq",
        ["ne"] = "neq",
        ["gt"] = "gt",
        ["ge"] = "gte",
        ["lt"] = "lt",
        ["le"] = "lte",
        ["between"] = "between",
        ["like"] = "contains",
        ["match"] = "contains",
        ["startswith"] = "startsWith",
        ["endswith"] = "endsWith",
        ["in"] = "in",
        ["nin"] = "nin",
        ["isnull"] = "isNull",
        ["includes"] = "includes",
    };

    private readonly IReadOnlyDictionary<string, SchemaFieldMetadata> _fields;

    public SchemaFilterContext(IReadOnlyDictionary<string, SchemaFieldMetadata> fields)
    {
        _fields = fields ?? throw new ArgumentNullException(nameof(fields));
    }

    /// <summary>
    /// Gets metadata for the given field path (dot-separated for nested fields).
    /// Returns null when the field is not declared in the schema.
    /// </summary>
    public SchemaFieldMetadata? GetFieldMetadata(string fieldPath)
    {
        return _fields.TryGetValue(fieldPath, out var metadata) ? metadata : null;
    }

    /// <summary>
    /// A field is filterable when it exists in the schema and has non-empty x-filterOperators.
    /// Fields not declared in the schema are considered non-filterable.
    /// </summary>
    public bool IsFieldFilterable(string fieldPath)
    {
        var metadata = GetFieldMetadata(fieldPath);
        return metadata is not null && metadata.IsFilterable;
    }

    /// <summary>
    /// Checks whether the given internal operator (e.g. "ge") is allowed for the field.
    /// Maps the internal operator name to the schema operator name (e.g. "gte") before checking.
    /// </summary>
    public bool IsOperatorAllowed(string fieldPath, string internalOperator)
    {
        var metadata = GetFieldMetadata(fieldPath);
        if (metadata is null || !metadata.IsFilterable)
            return false;

        var schemaOp = ToSchemaOperator(internalOperator);
        return metadata.FilterOperators.Contains(schemaOp, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A field is sortable when it exists in the schema and has x-sortable: true.
    /// </summary>
    public bool IsFieldSortable(string fieldPath)
    {
        var metadata = GetFieldMetadata(fieldPath);
        return metadata is not null && metadata.Sortable;
    }

    /// <summary>
    /// Maps an internal filter operator name to its schema-facing equivalent.
    /// </summary>
    public static string ToSchemaOperator(string internalOperator)
    {
        return InternalToSchemaOperatorMap.TryGetValue(internalOperator, out var schemaOp)
            ? schemaOp
            : internalOperator;
    }
}
