using System.Text.Json;
using System.Text.Json.Serialization;
using BBT.Workflow.Definitions.Schemas;

namespace BBT.Workflow.Definitions.GraphQL;

/// <summary>
/// Represents a GraphQL-style filter request with optional aggregations and groupBy
/// </summary>
public sealed class GraphQLFilterRequest
{
    /// <summary>
    /// Filter conditions in GraphQL-style JSON format
    /// Example: {"attributes":{"clientId":{"eq":122}}}
    /// </summary>
    [JsonPropertyName("filter")]
    public GraphQLFilterNode? Filter { get; set; }

    /// <summary>
    /// Group by configuration
    /// </summary>
    [JsonPropertyName("groupBy")]
    public GroupByRequest? GroupBy { get; set; }

    /// <summary>
    /// Aggregation functions to apply (without groupBy)
    /// </summary>
    [JsonPropertyName("aggregations")]
    public AggregationRequest? Aggregations { get; set; }

    /// <summary>
    /// Sort order for results (instance columns and/or attributes JSON path).
    /// </summary>
    [JsonPropertyName("orderBy")]
    public OrderByRequest? OrderBy { get; set; }

    /// <summary>
    /// Schema-driven filter/sort metadata resolved from the workflow's JSON Schema.
    /// When set, enables field filterability validation, operator whitelisting,
    /// sortability checks, and type-aware SQL casting.
    /// </summary>
    [JsonIgnore]
    public SchemaFilterContext? SchemaContext { get; set; }
}

/// <summary>
/// Single sort field with direction for orderBy
/// </summary>
/// <remarks>
/// Unmapped members are rejected so that a misspelled property (e.g. <c>fld</c> instead of
/// <c>field</c>) fails validation instead of being silently dropped into an unsorted query.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class OrderByField
{
    /// <summary>
    /// Field to sort by (e.g. "createdAt", "status", "attributes.clientId")
    /// </summary>
    [JsonPropertyName("field")]
    public string? Field { get; set; }

    /// <summary>
    /// Sort direction: "asc" or "desc" (case-insensitive). Defaults to "asc" if missing.
    /// </summary>
    [JsonPropertyName("direction")]
    public string? Direction { get; set; }
}

/// <summary>
/// OrderBy request: single field or multiple fields.
/// Supports {"field":"createdAt","direction":"desc"} or {"fields":[{"field":"status","direction":"asc"},...]}.
/// </summary>
/// <remarks>
/// Unmapped members are rejected so that a misspelled property fails validation instead of
/// silently degrading into the default <c>CreatedAt DESC</c> ordering.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class OrderByRequest
{
    /// <summary>
    /// Single field (when not using fields array)
    /// </summary>
    [JsonPropertyName("field")]
    public string? Field { get; set; }

    /// <summary>
    /// Direction for single field (when not using fields array)
    /// </summary>
    [JsonPropertyName("direction")]
    public string? Direction { get; set; }

    /// <summary>
    /// Multiple sort fields
    /// </summary>
    [JsonPropertyName("fields")]
    public List<OrderByField>? Fields { get; set; }

    /// <summary>
    /// Gets all order-by entries as a list for uniform handling (single or multi).
    /// </summary>
    public IReadOnlyList<(string Field, string Direction)> GetEntries()
    {
        if (Fields != null && Fields.Count > 0)
        {
            return Fields
                .Where(f => !string.IsNullOrWhiteSpace(f.Field))
                .Select(f => (f.Field!.Trim(), NormalizeDirection(f.Direction)))
                .ToList();
        }
        if (!string.IsNullOrWhiteSpace(Field))
        {
            return new List<(string, string)> { (Field.Trim(), NormalizeDirection(Direction)) };
        }
        return Array.Empty<(string, string)>();
    }

    private static string NormalizeDirection(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction))
            return "asc";
        return direction!.Trim().Equals("desc", StringComparison.OrdinalIgnoreCase) ? "desc" : "asc";
    }
}

/// <summary>
/// Represents a filter node in the GraphQL-style filter tree
/// Can be a logical operator (AND, OR, NOT) or a field condition
/// </summary>
[JsonConverter(typeof(GraphQLFilterNodeConverter))]
public class GraphQLFilterNode
{
    /// <summary>
    /// Logical AND operator - all conditions must match
    /// </summary>
    [JsonPropertyName("and")]
    public List<GraphQLFilterNode>? And { get; set; }

    /// <summary>
    /// Logical OR operator - at least one condition must match
    /// </summary>
    [JsonPropertyName("or")]
    public List<GraphQLFilterNode>? Or { get; set; }

    /// <summary>
    /// Logical NOT operator - condition must not match
    /// </summary>
    [JsonPropertyName("not")]
    public GraphQLFilterNode? Not { get; set; }

    /// <summary>
    /// Field conditions (e.g., "attributes": {"clientId": {"eq": 122}})
    /// </summary>
    [JsonPropertyName("attributes")]
    public Dictionary<string, FieldCondition>? Attributes { get; set; }

    /// <summary>
    /// Node-level property names the parser did not recognize and therefore dropped.
    /// </summary>
    /// <remarks>
    /// Populated by <see cref="GraphQLFilterNodeConverter"/> so that
    /// <c>InstanceQueryValidator</c> can reject the request instead of executing a query that
    /// silently ignores part of the caller's intent. Never serialized.
    /// </remarks>
    [JsonIgnore]
    public List<string>? UnrecognizedProperties { get; set; }

    /// <summary>
    /// Determines the type of this filter node
    /// </summary>
    public FilterNodeType NodeType
    {
        get
        {
            if (And != null && And.Count > 0) return FilterNodeType.And;
            if (Or != null && Or.Count > 0) return FilterNodeType.Or;
            if (Not != null) return FilterNodeType.Not;
            if (Attributes != null && Attributes.Count > 0) return FilterNodeType.Condition;
            return FilterNodeType.Empty;
        }
    }
}

/// <summary>
/// Type of filter node in the expression tree
/// </summary>
public enum FilterNodeType
{
    /// <summary>Empty or invalid node</summary>
    Empty,
    /// <summary>Logical AND operator</summary>
    And,
    /// <summary>Logical OR operator</summary>
    Or,
    /// <summary>Logical NOT operator</summary>
    Not,
    /// <summary>Field condition (leaf node)</summary>
    Condition
}

/// <summary>
/// An operator name the filter parser did not recognize, captured so it can be reported rather
/// than silently dropped.
/// </summary>
/// <param name="Name">The operator name as authored, lower-cased.</param>
/// <param name="ValueKind">
/// The JSON value kind that followed it. <see cref="JsonValueKind.Object"/> means the parser also
/// treated it as a nested field path, which is a legitimate construct for unreserved names.
/// </param>
public readonly record struct UnrecognizedOperator(string Name, JsonValueKind ValueKind);

/// <summary>
/// The canonical set of filter operator names the runtime can execute, and the misspellings it
/// knows how to correct.
/// </summary>
/// <remarks>
/// This is the single source of truth shared by the validator and the error messages. It must stay
/// in sync with the operator switch in <see cref="GraphQLFilterNodeConverter"/>; a parity test
/// pins the invariant.
/// </remarks>
public static class FilterOperators
{
    /// <summary>Operator names accepted on the wire, lower-cased for ordinal-ignore-case lookup.</summary>
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "ne", "gt", "ge", "lt", "le", "between", "like", "match",
        "startswith", "endswith", "in", "nin", "isNull", "includes"
    };

    /// <summary>
    /// Common misspellings mapped to the operator the caller almost certainly meant.
    /// </summary>
    /// <remarks>
    /// <c>gte</c>/<c>lte</c>/<c>neq</c>/<c>contains</c> are the schema-authoring spellings used by
    /// <c>SchemaFilterContext.InternalToSchemaOperatorMap</c> for <c>x-filterOperators</c>. They are
    /// deliberately <em>not</em> accepted as wire aliases: <c>contains</c> is ambiguous (both
    /// <c>like</c> and <c>match</c> map to it), so aliasing would have to silently guess.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> Suggestions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gte"] = "ge",
            ["lte"] = "le",
            ["neq"] = "ne",
            ["notequals"] = "ne",
            ["equals"] = "eq",
            ["contains"] = "like",
            ["notin"] = "nin",
            ["null"] = "isNull"
        };

    /// <summary>Supported operator names as a stable, comma-separated list for error messages.</summary>
    public static string SupportedList { get; } = string.Join(", ", Supported.OrderBy(o => o, StringComparer.Ordinal));

    /// <summary>Returns the suggested replacement for <paramref name="operatorName"/>, or null.</summary>
    public static string? Suggest(string? operatorName) =>
        !string.IsNullOrWhiteSpace(operatorName) && Suggestions.TryGetValue(operatorName, out var hint) ? hint : null;
}

/// <summary>
/// Represents a condition on a single field with an operator and value
/// </summary>
public class FieldCondition
{
    /// <summary>Equal to</summary>
    [JsonPropertyName("eq")]
    public object? Eq { get; set; }

    /// <summary>Not equal to</summary>
    [JsonPropertyName("ne")]
    public object? Ne { get; set; }

    /// <summary>Greater than</summary>
    [JsonPropertyName("gt")]
    public object? Gt { get; set; }

    /// <summary>Greater than or equal</summary>
    [JsonPropertyName("ge")]
    public object? Ge { get; set; }

    /// <summary>Less than</summary>
    [JsonPropertyName("lt")]
    public object? Lt { get; set; }

    /// <summary>Less than or equal</summary>
    [JsonPropertyName("le")]
    public object? Le { get; set; }

    /// <summary>Between two values (array of [min, max])</summary>
    [JsonPropertyName("between")]
    public object[]? Between { get; set; }

    /// <summary>Contains substring (case-insensitive)</summary>
    [JsonPropertyName("like")]
    public string? Like { get; set; }

    /// <summary>Alias for like</summary>
    [JsonPropertyName("match")]
    public string? Match { get; set; }

    /// <summary>Starts with prefix (case-insensitive)</summary>
    [JsonPropertyName("startswith")]
    public string? StartsWith { get; set; }

    /// <summary>Ends with suffix (case-insensitive)</summary>
    [JsonPropertyName("endswith")]
    public string? EndsWith { get; set; }

    /// <summary>Value in list</summary>
    [JsonPropertyName("in")]
    public object[]? In { get; set; }

    /// <summary>Value not in list</summary>
    [JsonPropertyName("nin")]
    public object[]? NotIn { get; set; }

    /// <summary>Null check (true = is null, false = is not null)</summary>
    [JsonPropertyName("isNull")]
    public bool? IsNull { get; set; }

    /// <summary>
    /// JSON array containment at this field path: the document must contain the path as a JSON array
    /// with at least one element that matches this partial object (PostgreSQL <c>jsonb @&gt;</c>).
    /// Cannot be combined with other operators on the same field.
    /// </summary>
    [JsonPropertyName("includes")]
    public JsonElement? Includes { get; set; }

    /// <summary>
    /// Nested field conditions for dot notation support
    /// Example: {"parent": {"child": {"eq": "value"}}}
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object>? NestedConditions { get; set; }

    /// <summary>
    /// Operator names the parser did not recognize and therefore dropped.
    /// </summary>
    /// <remarks>
    /// Populated by <see cref="GraphQLFilterNodeConverter"/>. Without this, an unsupported
    /// operator such as <c>gte</c> would leave the condition with zero operators, the WHERE
    /// clause would compile to nothing, and the query would return every row. Never serialized.
    /// </remarks>
    [JsonIgnore]
    public List<UnrecognizedOperator>? UnrecognizedOperators { get; set; }

    /// <summary>
    /// Gets the operator type and value from this condition
    /// </summary>
    public IEnumerable<(string Operator, object? Value)> GetOperators()
    {
        if (Eq != null) yield return ("eq", Eq);
        if (Ne != null) yield return ("ne", Ne);
        if (Gt != null) yield return ("gt", Gt);
        if (Ge != null) yield return ("ge", Ge);
        if (Lt != null) yield return ("lt", Lt);
        if (Le != null) yield return ("le", Le);
        if (Between != null) yield return ("between", Between);
        if (Like != null) yield return ("like", Like);
        if (Match != null) yield return ("match", Match);
        if (StartsWith != null) yield return ("startswith", StartsWith);
        if (EndsWith != null) yield return ("endswith", EndsWith);
        if (In != null) yield return ("in", In);
        if (NotIn != null) yield return ("nin", NotIn);
        if (IsNull.HasValue) yield return ("isNull", IsNull.Value);
        if (Includes is { ValueKind: JsonValueKind.Object } inc)
            yield return ("includes", inc);
    }
}

/// <summary>
/// GroupBy request configuration
/// </summary>
/// <remarks>
/// Unmapped members are rejected so that a misspelled property (e.g. <c>fieldz</c>) fails
/// validation instead of silently degrading the request into an ungrouped query.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class GroupByRequest
{
    /// <summary>
    /// Single field to group by (e.g., "attributes.status")
    /// </summary>
    [JsonPropertyName("field")]
    public string? Field { get; set; }

    /// <summary>
    /// Multiple fields to group by
    /// </summary>
    [JsonPropertyName("fields")]
    public List<string>? Fields { get; set; }

    /// <summary>
    /// Aggregation functions to apply to each group
    /// </summary>
    [JsonPropertyName("aggregations")]
    public AggregationRequest? Aggregations { get; set; }

    /// <summary>
    /// Gets all group by fields (combines single field and multiple fields)
    /// </summary>
    public List<string> GetFields()
    {
        var result = new List<string>();
        if (!string.IsNullOrEmpty(Field))
            result.Add(Field);
        if (Fields != null)
        {
            result.AddRange(Fields.Where(f => !string.IsNullOrWhiteSpace(f)));
        }
        return result.Distinct().ToList();
    }
}

/// <summary>
/// Aggregation functions request
/// </summary>
/// <remarks>
/// Unmapped members are rejected so that an unsupported aggregation function (e.g.
/// <c>median</c>) fails validation instead of deserializing to an empty request and silently
/// degrading into a plain, unaggregated list.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AggregationRequest
{
    /// <summary>
    /// Count of records (true for COUNT(*), or field name for COUNT(field))
    /// </summary>
    [JsonPropertyName("count")]
    public object? Count { get; set; }

    /// <summary>
    /// Sum of numeric field values (field name)
    /// </summary>
    [JsonPropertyName("sum")]
    public string? Sum { get; set; }

    /// <summary>
    /// Average of numeric field values (field name)
    /// </summary>
    [JsonPropertyName("avg")]
    public string? Avg { get; set; }

    /// <summary>
    /// Minimum value (field name)
    /// </summary>
    [JsonPropertyName("min")]
    public string? Min { get; set; }

    /// <summary>
    /// Maximum value (field name)
    /// </summary>
    [JsonPropertyName("max")]
    public string? Max { get; set; }

    /// <summary>
    /// Checks if any aggregation is requested
    /// </summary>
    [JsonIgnore]
    public bool HasAggregations => Count != null || Sum != null || Avg != null || Min != null || Max != null;
}

/// <summary>
/// Response for aggregation queries
/// </summary>
public sealed class AggregationResponse
{
    /// <summary>
    /// Count result
    /// </summary>
    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Count { get; set; }

    /// <summary>
    /// Sum result
    /// </summary>
    [JsonPropertyName("sum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Sum { get; set; }

    /// <summary>
    /// Average result
    /// </summary>
    [JsonPropertyName("avg")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Avg { get; set; }

    /// <summary>
    /// Minimum result
    /// </summary>
    [JsonPropertyName("min")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Min { get; set; }

    /// <summary>
    /// Maximum result
    /// </summary>
    [JsonPropertyName("max")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Max { get; set; }
}

/// <summary>
/// Response for group by queries with aggregations
/// </summary>
public sealed class GroupByResponse
{
    /// <summary>
    /// Group key values (one per groupBy field)
    /// </summary>
    [JsonPropertyName("keys")]
    public Dictionary<string, object?> Keys { get; set; } = new();

    /// <summary>
    /// Aggregation results for this group
    /// </summary>
    [JsonPropertyName("aggregations")]
    public AggregationResponse Aggregations { get; set; } = new();
}

/// <summary>
/// Combined filter response that can include data, aggregations, or grouped results
/// </summary>
public sealed class GraphQLFilterResponse<T>
{
    /// <summary>
    /// Filtered data (when no aggregation/groupBy is requested)
    /// </summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<T>? Data { get; set; }

    /// <summary>
    /// Aggregation results (when aggregations without groupBy is requested)
    /// </summary>
    [JsonPropertyName("aggregations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AggregationResponse? Aggregations { get; set; }

    /// <summary>
    /// Grouped results with aggregations
    /// </summary>
    [JsonPropertyName("groups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<GroupByResponse>? Groups { get; set; }

    /// <summary>
    /// Pagination info (when returning data)
    /// </summary>
    [JsonPropertyName("pagination")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PaginationInfo? Pagination { get; set; }
}

/// <summary>
/// Pagination information for filtered results
/// </summary>
public sealed class PaginationInfo
{
    /// <summary>Current page number</summary>
    [JsonPropertyName("page")]
    public int Page { get; set; }

    /// <summary>Page size</summary>
    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    /// <summary>Total record count</summary>
    [JsonPropertyName("totalCount")]
    public long TotalCount { get; set; }

    /// <summary>Total pages</summary>
    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }
}


