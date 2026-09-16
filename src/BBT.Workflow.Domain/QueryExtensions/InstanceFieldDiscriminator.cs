using BBT.Workflow.Instances;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Service to discriminate between Instance table columns and JSON Data fields
/// Used by filter services to route filters to the appropriate handler
/// </summary>
public static class InstanceFieldDiscriminator
{
    /// <summary>
    /// Instance table column names (case-insensitive)
    /// </summary>
    private static readonly HashSet<string> InstanceColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Id",
        "Key",
        "Flow",
        "CurrentState",
        "State", // Alias for EffectiveState
        "Status",
        // The propagated projection, not the clamped read-time answer Instance.GetEffectiveStatus
        // returns: a filter runs in SQL over the column as stored. See the note in the filtering doc.
        "EffectiveStatus",
        // Exposed under this name, NOT the bare "Type", and the alias is load-bearing. Columns and
        // data attributes share one dictionary here and are told apart purely by name (see
        // SeparateFilters / GraphQLJsonFilterService.BuildConditions), and ExtractFieldName strips
        // the "attributes=" prefix BEFORE the column check — so claiming "type" would silently
        // retarget every existing filter of every domain whose schema has a business field named
        // "type", with no escape hatch on the legacy surface. Aliased to the "Type" column below.
        "InstanceType",
        "CreatedAt",
        "ModifiedAt",
        "EffectiveState",
        "CompletedAt",
        "IsTransient",
        "EffectiveStateType",
        "EffectiveStateSubType",
        "CurrentStateType",
        "CurrentStateSubType",
        "Stage",
        "CreatedBy",
        "CreatedByBehalfOf",
        "ModifiedBy",
        "ModifiedByBehalfOf"
    };

    /// <summary>
    /// Physical column names that are reachable ONLY through an alias, i.e. that are deliberately
    /// not user-facing names in <see cref="InstanceColumns"/>.
    /// </summary>
    /// <remarks>
    /// <c>"Type"</c> is the one entry: the start-origin column is exposed to callers as
    /// <c>instanceType</c> so that a bare <c>type</c> keeps meaning the caller's own data attribute.
    /// Every other alias (<c>State</c> → <c>EffectiveState</c>, …) resolves to a name that is itself
    /// in <see cref="InstanceColumns"/>, which is why this set did not exist before.
    /// </remarks>
    private static readonly HashSet<string> AliasOnlyColumns = new(StringComparer.Ordinal)
    {
        "Type"
    };

    /// <summary>
    /// Status code to description mapping for filter value resolution
    /// </summary>
    private static readonly Dictionary<string, string> StatusNameToCode = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Active", "A" },
        { "Busy", "B" },
        { "Completed", "C" },
        { "Faulted", "F" },
        { "Passive", "P" },
        // Also support direct code values
        { "A", "A" },
        { "B", "B" },
        { "C", "C" },
        { "F", "F" },
        { "P", "P" }
    };

    /// <summary>
    /// Instance type name to code mapping for filter value resolution (see
    /// <see cref="StatusNameToCode"/> for the pattern).
    /// </summary>
    private static readonly Dictionary<string, string> TypeNameToCode = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Root", "R" },
        { "SubFlow", "S" },
        { "SubProcess", "P" },
        // Also support direct code values
        { "R", "R" },
        { "S", "S" },
        { "P", "P" }
    };

    /// <summary>
    /// Check if a field name is an Instance table column
    /// </summary>
    /// <param name="fieldName">Field name to check (e.g., "key", "status", "attributes")</param>
    /// <returns>True if the field is an Instance column, false if it's a JSON field</returns>
    public static bool IsInstanceColumn(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            return false;

        // Remove "attributes=" prefix if present (for legacy format compatibility)
        var cleanFieldName = fieldName.Replace("attributes=", "", StringComparison.OrdinalIgnoreCase);
        
        // Extract the first part of the field path (e.g., "key" from "key.subfield")
        var rootFieldName = cleanFieldName.Split('.')[0];
        
        // Handle alias: "State" -> "EffectiveState"
        if (fieldName.Equals("State", StringComparison.OrdinalIgnoreCase))
            rootFieldName = "EffectiveState";
        
        // Handle alias: "StateType" -> "EffectiveStateType"
        if (fieldName.Equals("StateType", StringComparison.OrdinalIgnoreCase))
            rootFieldName = "EffectiveStateType";
        
        // Handle alias: "StateSubType" -> "EffectiveStateSubType"
        if (fieldName.Equals("StateSubType", StringComparison.OrdinalIgnoreCase))
            rootFieldName = "EffectiveStateSubType";

        return InstanceColumns.Contains(rootFieldName);
    }

    /// <summary>
    /// True when <paramref name="columnName"/> is a physical Instance column name that
    /// <see cref="GetInstanceColumnName"/> has already resolved.
    /// </summary>
    /// <remarks>
    /// Guard for callers that resolve the caller-supplied name first and only then build the
    /// condition (<c>GraphQLJsonFilterService.BuildInstanceFieldConditions</c> does exactly that).
    /// Re-checking a resolved name against <see cref="IsInstanceColumn"/> alone rejects every
    /// alias-only column — it threw <c>Invalid Instance column name: Type</c> for a perfectly valid
    /// <c>instanceType</c> filter. Deliberately SEPARATE from <see cref="IsInstanceColumn"/>: the
    /// discrimination surfaces must keep answering false for a bare <c>type</c>, or a caller's data
    /// attribute of that name would be hijacked into the column.
    /// </remarks>
    public static bool IsResolvedInstanceColumn(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            return false;

        return AliasOnlyColumns.Contains(fieldName) || IsInstanceColumn(fieldName);
    }

    /// <summary>
    /// Get the properly cased Instance column name for SQL generation
    /// </summary>
    /// <param name="fieldName">Field name (e.g., "key", "status", "createdAt")</param>
    /// <returns>Properly cased column name (e.g., "Key", "Status", "CreatedAt")</returns>
    public static string GetInstanceColumnName(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            throw new ArgumentException("Field name cannot be null or empty", nameof(fieldName));

        // Handle alias: "State" -> "EffectiveState"
        if (fieldName.Equals("State", StringComparison.OrdinalIgnoreCase))
            return "EffectiveState";
        
        // Handle alias: "StateType" -> "EffectiveStateType"
        if (fieldName.Equals("StateType", StringComparison.OrdinalIgnoreCase))
            return "EffectiveStateType";
        
        // Handle alias: "StateSubType" -> "EffectiveStateSubType"
        if (fieldName.Equals("StateSubType", StringComparison.OrdinalIgnoreCase))
            return "EffectiveStateSubType";

        // Handle alias: "InstanceType" -> "Type". The column is named Type; the filter name is not,
        // so that it cannot collide with a business attribute called "type". See the whitelist note.
        if (fieldName.Equals("InstanceType", StringComparison.OrdinalIgnoreCase))
            return "Type";

        // Idempotent for an alias-only column: a caller that resolved the name first and passes the
        // result back in (the GraphQL condition path) must get the same answer, not an exception.
        if (AliasOnlyColumns.Contains(fieldName))
            return fieldName;

        // Find the matching column name (case-insensitive match)
        var matchedColumn = InstanceColumns.FirstOrDefault(c => 
            c.Equals(fieldName, StringComparison.OrdinalIgnoreCase));

        if (matchedColumn == null)
            throw new ArgumentException($"Field '{fieldName}' is not a valid Instance column", nameof(fieldName));

        return matchedColumn;
    }

    /// <summary>
    /// Separate filters into Instance column filters and JSON Data filters
    /// </summary>
    /// <param name="filters">Array of filter strings</param>
    /// <returns>Tuple of (instanceFilters, jsonFilters)</returns>
    public static (string[] instanceFilters, string[] jsonFilters) SeparateFilters(string[] filters)
    {
        if (filters == null || filters.Length == 0)
            return (Array.Empty<string>(), Array.Empty<string>());

        var instanceFilters = new List<string>();
        var jsonFilters = new List<string>();

        foreach (var filter in filters)
        {
            if (string.IsNullOrWhiteSpace(filter))
                continue;

            // Extract field name from filter string
            // Format: "field=operator:value" or "attributes=field=operator:value"
            var fieldName = ExtractFieldName(filter);

            if (IsInstanceColumn(fieldName))
            {
                // Remove "attributes=" prefix if present for Instance columns
                var cleanFilter = filter.Replace("attributes=", "", StringComparison.OrdinalIgnoreCase);
                instanceFilters.Add(cleanFilter);
            }
            else
            {
                jsonFilters.Add(filter);
            }
        }

        return (instanceFilters.ToArray(), jsonFilters.ToArray());
    }

    /// <summary>
    /// Resolve Status value to database code
    /// Handles both status names (Active, Busy) and codes (A, B)
    /// </summary>
    /// <param name="statusValue">Status value from filter (e.g., "Active", "A")</param>
    /// <returns>Status code for database (e.g., "A")</returns>
    public static string ResolveStatusValue(string statusValue)
    {
        if (string.IsNullOrWhiteSpace(statusValue))
            return statusValue;

        // Try to resolve from mapping
        if (StatusNameToCode.TryGetValue(statusValue, out var code))
            return code;

        // Return as-is if not found (will be validated by database)
        return statusValue;
    }

    /// <summary>
    /// Resolve multiple Status values for IN/NIN operators
    /// </summary>
    /// <param name="statusValues">Array of status values</param>
    /// <returns>Array of status codes</returns>
    public static string[] ResolveStatusValues(string[] statusValues)
    {
        if (statusValues == null || statusValues.Length == 0)
            return statusValues ?? Array.Empty<string>();

        return statusValues.Select(ResolveStatusValue).ToArray();
    }

    /// <summary>
    /// Resolve an instance Type value to its database code.
    /// Handles both names (Root, SubFlow, SubProcess) and codes (R, S, P).
    /// </summary>
    /// <param name="typeValue">Type value from filter (e.g., "Root", "R")</param>
    /// <returns>Type code for database (e.g., "R")</returns>
    public static string ResolveTypeValue(string typeValue)
    {
        if (string.IsNullOrWhiteSpace(typeValue))
            return typeValue;

        // Try to resolve from mapping
        if (TypeNameToCode.TryGetValue(typeValue, out var code))
            return code;

        // Return as-is if not found (will be validated by database)
        return typeValue;
    }

    /// <summary>
    /// Resolve multiple instance Type values for IN/NIN operators
    /// </summary>
    /// <param name="typeValues">Array of type values</param>
    /// <returns>Array of type codes</returns>
    public static string[] ResolveTypeValues(string[] typeValues)
    {
        if (typeValues == null || typeValues.Length == 0)
            return typeValues ?? Array.Empty<string>();

        return typeValues.Select(ResolveTypeValue).ToArray();
    }


    /// <summary>
    /// Extract field name from filter string
    /// </summary>
    /// <param name="filter">Filter string (e.g., "key=eq:123" or "attributes=field=eq:value")</param>
    /// <returns>Field name (e.g., "key", "field")</returns>
    private static string ExtractFieldName(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return string.Empty;

        // Remove "attributes=" prefix if present
        var cleanFilter = filter!;
        if (filter.StartsWith("attributes=", StringComparison.OrdinalIgnoreCase))
        {
            cleanFilter = filter.Substring("attributes=".Length);
        }

        // Extract field name (everything before the first '=')
        var equalIndex = cleanFilter.IndexOf('=');
        if (equalIndex > 0)
        {
            return cleanFilter[..equalIndex];
        }

        return cleanFilter ?? string.Empty;
    }

    /// <summary>
    /// Check if a field name is the 'attributes' keyword (for JSON data)
    /// This helps distinguish between Instance columns and JSON data fields
    /// </summary>
    /// <param name="fieldName">Field name to check</param>
    /// <returns>True if field is 'attributes', false otherwise</returns>
    public static bool IsAttributesField(string fieldName)
    {
        return fieldName.Equals("attributes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get all supported Instance column names
    /// </summary>
    /// <returns>List of supported column names</returns>
    public static IEnumerable<string> GetSupportedColumns()
    {
        return InstanceColumns.OrderBy(c => c);
    }
}

