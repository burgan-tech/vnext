using System.Globalization;
using System.Text.Json;

namespace BBT.Workflow.Definitions.GraphQL;

/// <summary>
/// Parser for GraphQL-style JSON filter syntax
/// Converts JSON filter expressions to internal filter representation
/// </summary>
public static class GraphQLFilterParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Parse a JSON filter string into a GraphQLFilterNode tree
    /// </summary>
    /// <param name="jsonFilter">JSON filter string</param>
    /// <returns>Parsed filter node</returns>
    public static GraphQLFilterNode? ParseFilter(string? jsonFilter)
    {
        if (string.IsNullOrWhiteSpace(jsonFilter))
            return null;

        try
        {
            return JsonSerializer.Deserialize<GraphQLFilterNode>(jsonFilter, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Invalid JSON filter format: {ex.Message}", nameof(jsonFilter), ex);
        }
    }

    /// <summary>
    /// Parse a groupBy JSON string into a GroupByRequest
    /// </summary>
    /// <param name="jsonGroupBy">JSON groupBy string</param>
    /// <returns>Parsed groupBy request</returns>
    public static GroupByRequest? ParseGroupBy(string? jsonGroupBy)
    {
        if (string.IsNullOrWhiteSpace(jsonGroupBy))
            return null;

        try
        {
            return JsonSerializer.Deserialize<GroupByRequest>(jsonGroupBy, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Invalid JSON groupBy format: {ex.Message}", nameof(jsonGroupBy), ex);
        }
    }

    /// <summary>
    /// Parse an aggregations JSON string into an AggregationRequest
    /// </summary>
    /// <param name="jsonAggregations">JSON aggregations string</param>
    /// <returns>Parsed aggregation request</returns>
    public static AggregationRequest? ParseAggregations(string? jsonAggregations)
    {
        if (string.IsNullOrWhiteSpace(jsonAggregations))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AggregationRequest>(jsonAggregations, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Invalid JSON aggregations format: {ex.Message}", nameof(jsonAggregations), ex);
        }
    }

    /// <summary>
    /// Tries to parse a filter string as GraphQLFilterRequest format
    /// Returns true if the string is in GraphQLFilterRequest format (contains "filter" and/or "groupBy" properties)
    /// </summary>
    /// <param name="filterString">Filter string to check</param>
    /// <param name="request">Parsed request if successful, null otherwise</param>
    /// <returns>True if the string is in GraphQLFilterRequest format</returns>
    public static bool TryParseRequest(string? filterString, out GraphQLFilterRequest? request)
    {
        request = null;
        
        if (string.IsNullOrWhiteSpace(filterString))
            return false;

        try
        {
            // Try to deserialize as GraphQLFilterRequest
            var parsed = JsonSerializer.Deserialize<GraphQLFilterRequest>(filterString, JsonOptions);
            
            if (parsed == null)
                return false;

            // Check if it has filter or groupBy properties (GraphQLFilterRequest format)
            // If it only has filter property with attributes, it might be a regular GraphQL filter
            // So we check if it has groupBy or aggregations to distinguish
            if (parsed.GroupBy != null || parsed.Aggregations != null)
            {
                request = parsed;
                return true;
            }

            // If it has filter property but it's structured as GraphQLFilterRequest
            // (not just a filter node), consider it as GraphQLFilterRequest format
            if (parsed.Filter != null)
            {
                // Additional check: if the original string contains "groupBy" or "aggregations" keys,
                // it's definitely GraphQLFilterRequest format
                var lowerString = filterString.ToLowerInvariant();
                if (lowerString.Contains("\"groupby\"") || lowerString.Contains("\"aggregations\""))
                {
                    request = parsed;
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parse a complete filter request from separate query parameters
    /// </summary>
    /// <param name="filter">Filter JSON string</param>
    /// <param name="groupBy">GroupBy JSON string</param>
    /// <param name="aggregations">Aggregations JSON string</param>
    /// <param name="orderBy">OrderBy JSON string (optional)</param>
    /// <returns>Complete filter request</returns>
    public static GraphQLFilterRequest ParseRequest(string? filter, string? groupBy, string? aggregations, string? orderBy = null)
    {
        return new GraphQLFilterRequest
        {
            Filter = ParseFilter(filter),
            GroupBy = ParseGroupBy(groupBy),
            Aggregations = ParseAggregations(aggregations),
            OrderBy = ParseOrderBy(orderBy)
        };
    }

    /// <summary>
    /// Parse an orderBy JSON string into an OrderByRequest.
    /// Supports single: {"field":"createdAt","direction":"desc"}
    /// or multiple: {"fields":[{"field":"status","direction":"asc"},{"field":"createdAt","direction":"desc"}]}.
    /// </summary>
    /// <param name="jsonOrderBy">OrderBy JSON string</param>
    /// <returns>Parsed orderBy request, or null if empty/invalid</returns>
    public static OrderByRequest? ParseOrderBy(string? jsonOrderBy)
    {
        if (string.IsNullOrWhiteSpace(jsonOrderBy))
            return null;

        try
        {
            var parsed = JsonSerializer.Deserialize<OrderByRequest>(jsonOrderBy, JsonOptions);
            if (parsed == null)
                return null;
            if (parsed.GetEntries().Count == 0)
                return null;
            return parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Converts a GraphQLFilterNode to a list of simple filter strings
    /// for backward compatibility with existing PostgreSqlJsonFilterService
    /// </summary>
    /// <param name="node">GraphQL filter node</param>
    /// <returns>List of filter strings in format "field=operator:value"</returns>
    public static List<ParsedCondition> FlattenToConditions(GraphQLFilterNode? node)
    {
        var conditions = new List<ParsedCondition>();
        if (node == null)
            return conditions;

        FlattenNodeRecursive(node, conditions, "", LogicalOperator.And);
        return conditions;
    }

    private static void FlattenNodeRecursive(
        GraphQLFilterNode node,
        List<ParsedCondition> conditions,
        string parentPath,
        LogicalOperator parentOperator)
    {
        switch (node.NodeType)
        {
            case FilterNodeType.And:
                foreach (var child in node.And!)
                {
                    FlattenNodeRecursive(child, conditions, parentPath, LogicalOperator.And);
                }
                break;

            case FilterNodeType.Or:
                var orGroup = new List<ParsedCondition>();
                foreach (var child in node.Or!)
                {
                    var childConditions = new List<ParsedCondition>();
                    FlattenNodeRecursive(child, childConditions, parentPath, LogicalOperator.Or);
                    orGroup.AddRange(childConditions);
                }
                // Mark these conditions as part of an OR group
                foreach (var condition in orGroup)
                {
                    condition.LogicalOperator = LogicalOperator.Or;
                    condition.GroupId = Guid.NewGuid().ToString();
                }
                conditions.AddRange(orGroup);
                break;

            case FilterNodeType.Not:
                var notConditions = new List<ParsedCondition>();
                FlattenNodeRecursive(node.Not!, notConditions, parentPath, LogicalOperator.Not);
                foreach (var condition in notConditions)
                {
                    condition.IsNegated = true;
                }
                conditions.AddRange(notConditions);
                break;

            case FilterNodeType.Condition:
                if (node.Attributes != null)
                {
                    foreach (var (fieldName, fieldCondition) in node.Attributes)
                    {
                        var fullPath = string.IsNullOrEmpty(parentPath) 
                            ? fieldName 
                            : $"{parentPath}.{fieldName}";

                        // Check for nested conditions first
                        if (fieldCondition.NestedConditions != null)
                        {
                            ProcessNestedConditions(fieldCondition.NestedConditions, fullPath, conditions);
                        }

                        // Process direct operators
                        foreach (var (op, value) in fieldCondition.GetOperators())
                        {
                            conditions.Add(new ParsedCondition
                            {
                                Field = fullPath,
                                Operator = op,
                                Value = ConvertValueToString(value, op),
                                LogicalOperator = parentOperator
                            });
                        }
                    }
                }
                break;
            default:
                // Unknown node type, ignore
                break;
        }
    }

    private static void ProcessNestedConditions(
        Dictionary<string, object> nestedConditions,
        string parentPath,
        List<ParsedCondition> conditions)
    {
        foreach (var (nestedField, nestedValue) in nestedConditions)
        {
            var fullPath = $"{parentPath}.{nestedField}";

            if (nestedValue is JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == JsonValueKind.Object)
                {
                    // This could be either operators or more nesting
                    foreach (var prop in jsonElement.EnumerateObject())
                    {
                        if (IsOperator(prop.Name))
                        {
                            conditions.Add(new ParsedCondition
                            {
                                Field = fullPath,
                                Operator = prop.Name.ToLowerInvariant(),
                                Value = ConvertJsonElementToString(prop.Value, prop.Name.ToLowerInvariant()),
                                LogicalOperator = LogicalOperator.And
                            });
                        }
                        else
                        {
                            // More nesting
                            var subPath = $"{fullPath}.{prop.Name}";
                            ProcessJsonElement(prop.Value, subPath, conditions);
                        }
                    }
                }
            }
        }
    }

    private static void ProcessJsonElement(JsonElement element, string path, List<ParsedCondition> conditions)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in element.EnumerateObject())
        {
            if (IsOperator(prop.Name))
            {
                conditions.Add(new ParsedCondition
                {
                    Field = path,
                    Operator = prop.Name.ToLowerInvariant(),
                    Value = ConvertJsonElementToString(prop.Value, prop.Name.ToLowerInvariant()),
                    LogicalOperator = LogicalOperator.And
                });
            }
            else
            {
                // More nesting
                var subPath = $"{path}.{prop.Name}";
                ProcessJsonElement(prop.Value, subPath, conditions);
            }
        }
    }

    private static bool IsOperator(string name)
    {
        var lowerName = name.ToLowerInvariant();
        return lowerName switch
        {
            "eq" or "ne" or "gt" or "ge" or "lt" or "le" or
            "between" or "like" or "match" or "startswith" or "endswith" or
            "in" or "nin" or "isnull" => true,
            _ => false
        };
    }

    private static string ConvertValueToString(object? value, string operatorType)
    {
        if (value == null)
            return string.Empty;

        // Handle arrays for between, in, nin
        if (value is object[] arr)
        {
            return string.Join(",", arr.Select(v => ConvertSingleValue(v)));
        }

        return ConvertSingleValue(value);
    }

    private static string ConvertSingleValue(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string s => s,
            bool b => b.ToString().ToLowerInvariant(),
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double dbl => dbl.ToString(CultureInfo.InvariantCulture),
            float f => f.ToString(CultureInfo.InvariantCulture),
            JsonElement je => ConvertJsonElementToString(je, "eq"),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string ConvertJsonElementToString(JsonElement element, string operatorType)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            JsonValueKind.Array => string.Join(",", element.EnumerateArray().Select(e => ConvertJsonElementToString(e, operatorType))),
            _ => element.GetRawText()
        };
    }
}

/// <summary>
/// Represents a parsed filter condition
/// </summary>
public sealed class ParsedCondition
{
    /// <summary>
    /// Field path (e.g., "clientId" or "parent.child")
    /// </summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>
    /// Operator type (eq, ne, gt, etc.)
    /// </summary>
    public string Operator { get; set; } = string.Empty;

    /// <summary>
    /// Value as string
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Logical operator combining this with other conditions
    /// </summary>
    public LogicalOperator LogicalOperator { get; set; } = LogicalOperator.And;

    /// <summary>
    /// Whether this condition is negated (NOT)
    /// </summary>
    public bool IsNegated { get; set; }

    /// <summary>
    /// Group ID for OR conditions
    /// </summary>
    public string? GroupId { get; set; }

    /// <summary>
    /// Converts to legacy filter format: "field=operator:value"
    /// </summary>
    public string ToLegacyFormat()
    {
        return $"attributes={Field}={Operator}:{Value}";
    }
}

/// <summary>
/// Logical operator for combining conditions
/// </summary>
public enum LogicalOperator
{
    /// <summary>All conditions must match</summary>
    And,
    /// <summary>At least one condition must match</summary>
    Or,
    /// <summary>Condition must not match</summary>
    Not
}


