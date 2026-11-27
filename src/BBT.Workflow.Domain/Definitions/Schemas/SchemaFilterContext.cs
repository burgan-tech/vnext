namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// Holds the full field metadata map parsed from a workflow's JSON Schema and provides
/// validation methods for filter/sort operations.
/// </summary>
public sealed class SchemaFilterContext
{
    /// <summary>
    /// Query operator token → the name a schema declares in <c>x-filterOperators</c>. Shared with the publish-time
    /// check (<see cref="FilterOperatorDefinition"/>), which refuses a declaration spelled as a query token.
    /// </summary>
    internal static readonly Dictionary<string, string> InternalToSchemaOperatorMap = new(StringComparer.OrdinalIgnoreCase)
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

    public bool EnforceFiltering { get; init; } = true;

    /// <summary>
    /// Paths declared <c>x-encryption.type: "encrypt"</c>. The database holds ciphertext there, so a filter, sort,
    /// groupBy or aggregation on such a path — or on one of its ancestors, whose <c>@&gt;</c> containment would
    /// reach it — is refused whatever <see cref="EnforceFiltering"/> says. Refused rather than answered with zero
    /// rows: a silent empty result ("no instance for this customer yet") is how duplicates get created.
    /// </summary>
    public IReadOnlySet<string> EncryptedPaths { get; init; } = new HashSet<string>();

    /// <summary>True when <paramref name="fieldPath"/> is an encrypted path or an ancestor of one.</summary>
    public bool IsEncryptedOrAncestor(string fieldPath)
    {
        if (EncryptedPaths.Count == 0 || string.IsNullOrEmpty(fieldPath))
            return false;
        if (EncryptedPaths.Contains(fieldPath))
            return true;

        var prefix = fieldPath + ".";
        foreach (var path in EncryptedPaths)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
    /// <summary>
    /// Data paths the CALLER of this query may not read as stored: an <c>x-roles</c> path its roles do not open, or
    /// an <c>x-masking</c> path it is not exempt from. Empty for a context that is not bound to a caller (row
    /// security, publish checks). A filter, sort, groupBy or aggregation runs on the stored value and answers for a
    /// set of rows, so on such a path — or on a path under it, or on an ancestor that contains it — it is refused
    /// whatever <see cref="EnforceFiltering"/> says: a group key or a <c>min</c>/<c>max</c> returns the value
    /// outright, and a filter or a sort order is an oracle on it. A runtime-planned row-security leaf
    /// (<c>policyExempt</c>) is policy, not caller input, and is never checked against this set.
    /// </summary>
    public IReadOnlySet<string> RestrictedPaths { get; init; } = new HashSet<string>();

    /// <summary>
    /// True when <paramref name="fieldPath"/> is a restricted path, sits under one, or is an ancestor of one.
    /// </summary>
    /// <param name="fieldPath">Data path relative to the instance data root (dot-separated, no <c>attributes.</c> prefix).</param>
    public bool IsRestrictedOrRelated(string fieldPath)
    {
        if (RestrictedPaths.Count == 0 || string.IsNullOrEmpty(fieldPath))
            return false;
        if (RestrictedPaths.Contains(fieldPath))
            return true;

        var prefix = fieldPath + ".";
        return RestrictedPaths.Any(path =>
            path.StartsWith(prefix, StringComparison.Ordinal) ||
            fieldPath.StartsWith(path + ".", StringComparison.Ordinal));
    }

    /// <summary>
    /// Refuses a caller-supplied query field that touches a restricted path (<see cref="IsRestrictedOrRelated"/>).
    /// </summary>
    /// <param name="fieldPath">Data path relative to the instance data root.</param>
    /// <param name="usage">What the query does with the field, for the message: <c>filtered</c>, <c>sorted</c>,
    /// <c>grouped or aggregated</c>.</param>
    /// <exception cref="BBT.Workflow.ExceptionHandling.SchemaFilterValidationException">The path is restricted.</exception>
    public void EnsureNotRestricted(string fieldPath, string usage)
    {
        if (IsRestrictedOrRelated(fieldPath))
            throw new BBT.Workflow.ExceptionHandling.SchemaFilterValidationException(
                $"Field '{fieldPath}' is restricted for the caller (x-roles / x-masking) and cannot be {usage}.")
            {
                CallerRestricted = true
            };
    }

    /// <summary>
    /// A copy of this context bound to a caller: same fields, policy switch, encrypted paths and ready indexes,
    /// with <paramref name="restrictedPaths"/> as <see cref="RestrictedPaths"/>.
    /// </summary>
    /// <param name="restrictedPaths">The paths the caller may not read as stored.</param>
    public SchemaFilterContext WithRestrictedPaths(IReadOnlySet<string> restrictedPaths) => new(_fields)
    {
        EnforceFiltering = EnforceFiltering,
        EncryptedPaths = EncryptedPaths,
        ReadyIndexes = ReadyIndexes,
        RestrictedPaths = restrictedPaths ?? throw new ArgumentNullException(nameof(restrictedPaths))
    };

    public IReadOnlyDictionary<string, SchemaFieldMetadata> Fields => _fields;
    public IReadOnlySet<string> ReadyIndexes { get; init; } = new HashSet<string>();

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
        if (IsEncryptedOrAncestor(fieldPath) || IsRestrictedOrRelated(fieldPath)) return false;
        var metadata = GetFieldMetadata(fieldPath);
        return !EnforceFiltering || (metadata is not null && metadata.IsFilterable);
    }

    /// <summary>
    /// Checks whether the given internal operator (e.g. "ge") is allowed for the field.
    /// Maps the internal operator name to the schema operator name (e.g. "gte") before checking.
    /// </summary>
    public bool IsOperatorAllowed(string fieldPath, string internalOperator)
    {
        if (IsEncryptedOrAncestor(fieldPath) || IsRestrictedOrRelated(fieldPath)) return false;
        if (!EnforceFiltering) return true;
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
        if (IsEncryptedOrAncestor(fieldPath) || IsRestrictedOrRelated(fieldPath)) return false;
        var metadata = GetFieldMetadata(fieldPath);
        return !EnforceFiltering || (metadata is not null && metadata.Sortable);
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
