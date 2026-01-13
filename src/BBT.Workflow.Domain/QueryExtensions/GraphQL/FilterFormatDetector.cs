using System.Text.RegularExpressions;

namespace BBT.Workflow.Definitions.GraphQL;

/// <summary>
/// Detects the format of incoming filter strings and provides unified handling
/// Supports both legacy format (attributes=field=eq:value) and new GraphQL-style JSON format
/// </summary>
public static class FilterFormatDetector
{
    /// <summary>
    /// Legacy filter format pattern: attributes=field=operator:value or field=operator:value
    /// Supports Instance column names (key, status, flow, createdAt, etc.)
    /// </summary>
    private static readonly Regex LegacyFilterPattern = new(
        @"^(attributes=|key=|status=|flow=|currentstate=|state=|createdat=|modifiedat=|completedat=|istransient=)?[a-zA-Z0-9._]+=(?:eq|ne|gt|ge|lt|le|between|match|like|startswith|endswith|in|nin):.+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Detects the format of a filter string
    /// </summary>
    /// <param name="filter">Filter string to analyze</param>
    /// <returns>Detected filter format</returns>
    public static FilterFormat DetectFormat(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return FilterFormat.Empty;

        var trimmed = filter.Trim();

        // Check for JSON format - starts with { and ends with }
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            return FilterFormat.GraphQL;
        }

        // Check for legacy format using pattern
        try
        {
            if (LegacyFilterPattern.IsMatch(trimmed))
            {
                return FilterFormat.Legacy;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Timeout indicates potential ReDoS attack, treat as invalid
            return FilterFormat.Empty;
        }

        // Try to detect JSON array format
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            return FilterFormat.GraphQL;
        }

        // Return Empty for unrecognized formats instead of defaulting to Legacy
        // This prevents malformed input from being passed to PostgreSqlJsonFilterService
        // which expects valid legacy format and can cause unexpected parsing errors
        return FilterFormat.Empty;
    }

    /// <summary>
    /// Detects format for an array of filters
    /// Returns the dominant format, with GraphQL taking precedence if any are GraphQL
    /// </summary>
    public static FilterFormat DetectFormat(string[]? filters)
    {
        if (filters == null || filters.Length == 0)
            return FilterFormat.Empty;

        var hasGraphQL = false;
        var hasLegacy = false;

        foreach (var filter in filters)
        {
            if (string.IsNullOrWhiteSpace(filter))
                continue;

            var format = DetectFormat(filter);
            if (format == FilterFormat.GraphQL)
                hasGraphQL = true;
            else if (format == FilterFormat.Legacy)
                hasLegacy = true;
        }

        // GraphQL takes precedence - if any filter is GraphQL, treat all as GraphQL
        if (hasGraphQL)
            return FilterFormat.GraphQL;
        
        if (hasLegacy)
            return FilterFormat.Legacy;

        return FilterFormat.Empty;
    }

    /// <summary>
    /// Checks if a filter string is in GraphQL-style JSON format
    /// </summary>
    public static bool IsGraphQLFormat(string? filter)
    {
        return DetectFormat(filter) == FilterFormat.GraphQL;
    }

    /// <summary>
    /// Checks if a filter string is in legacy format
    /// </summary>
    public static bool IsLegacyFormat(string? filter)
    {
        return DetectFormat(filter) == FilterFormat.Legacy;
    }

    /// <summary>
    /// Combines multiple GraphQL filter nodes into a single AND node
    /// </summary>
    public static GraphQLFilterNode? CombineFilters(string[]? filters)
    {
        if (filters == null || filters.Length == 0)
            return null;

        var nodes = new List<GraphQLFilterNode>();

        foreach (var filter in filters)
        {
            if (string.IsNullOrWhiteSpace(filter))
                continue;

            var node = GraphQLFilterParser.ParseFilter(filter);
            if (node != null && node.NodeType != FilterNodeType.Empty)
            {
                nodes.Add(node);
            }
        }

        if (nodes.Count == 0)
            return null;

        if (nodes.Count == 1)
            return nodes[0];

        // Combine multiple nodes with AND
        return new GraphQLFilterNode
        {
            And = nodes
        };
    }

    /// <summary>
    /// Converts legacy filter strings to GraphQL filter format
    /// </summary>
    public static GraphQLFilterNode? ConvertLegacyToGraphQL(string[]? legacyFilters)
    {
        if (legacyFilters == null || legacyFilters.Length == 0)
            return null;

        var attributes = new Dictionary<string, FieldCondition>();

        foreach (var filter in legacyFilters)
        {
            if (string.IsNullOrWhiteSpace(filter))
                continue;

            try
            {
                var (field, op, value) = FilterOperatorParser.ParseOperator(filter);
                
                if (string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(op))
                    continue;

                if (!attributes.TryGetValue(field, out var condition))
                {
                    condition = new FieldCondition();
                    attributes[field] = condition;
                }

                SetOperatorValue(condition, op, value);
            }
            catch (ArgumentException)
            {
                // Skip invalid filters with argument errors
            }
            catch (FormatException)
            {
                // Skip invalid filters with format errors
            }
        }

        if (attributes.Count == 0)
            return null;

        return new GraphQLFilterNode
        {
            Attributes = attributes
        };
    }

    private static void SetOperatorValue(FieldCondition condition, string op, string value)
    {
        switch (op.ToLowerInvariant())
        {
            case "eq":
                condition.Eq = ParseValue(value);
                break;
            case "ne":
                condition.Ne = ParseValue(value);
                break;
            case "gt":
                condition.Gt = ParseValue(value);
                break;
            case "ge":
                condition.Ge = ParseValue(value);
                break;
            case "lt":
                condition.Lt = ParseValue(value);
                break;
            case "le":
                condition.Le = ParseValue(value);
                break;
            case "between":
                var parts = value.Split(',').Select(v => ParseValue(v.Trim())).ToArray();
                condition.Between = parts;
                break;
            case "like":
                condition.Like = value;
                break;
            case "match":
                condition.Match = value;
                break;
            case "startswith":
                condition.StartsWith = value;
                break;
            case "endswith":
                condition.EndsWith = value;
                break;
            case "in":
                condition.In = value.Split(',').Select(v => ParseValue(v.Trim())).ToArray();
                break;
            case "nin":
                condition.NotIn = value.Split(',').Select(v => ParseValue(v.Trim())).ToArray();
                break;
            default:
                // Unknown operator, ignore
                break;
        }
    }

    private static object ParseValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        // Try to parse as number
        if (long.TryParse(value, out var longVal))
            return longVal;
        
        if (decimal.TryParse(value, out var decVal))
            return decVal;

        // Try to parse as boolean
        if (bool.TryParse(value, out var boolVal))
            return boolVal;

        // Return as string
        return value;
    }
}

/// <summary>
/// Detected filter format
/// </summary>
public enum FilterFormat
{
    /// <summary>Empty or null filter</summary>
    Empty,
    
    /// <summary>Legacy format: attributes=field=eq:value</summary>
    Legacy,
    
    /// <summary>GraphQL-style JSON format: {"attributes":{"field":{"eq":"value"}}}</summary>
    GraphQL
}


