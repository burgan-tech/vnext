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
        "Key",
        "Flow",
        "CurrentState",
        "State", // Alias for EffectiveState
        "Status",
        "CreatedAt",
        "ModifiedAt",
        "CompletedAt",
        "IsTransient",
        "EffectiveStateType",
        "EffectiveStateSubType"
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

