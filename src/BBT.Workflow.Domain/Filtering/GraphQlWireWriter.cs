using System.Text.Json;

namespace BBT.Workflow.Filtering;

/// <summary>
/// Serializes the fluent filter model into the GraphQL-style wire JSON the list endpoint already
/// consumes. This is the inverse of the parser used server-side: it lets scripts author queries with
/// <see cref="InstanceQuery"/> and produce byte-for-byte the shape the existing API expects
/// (<c>{"and":[{"attributes":{"field":{"eq":…}}},{"currentState":{"eq":…}}]}</c>, groupBy, etc.),
/// so no endpoint/engine change is needed.
/// </summary>
internal static class GraphQlWireWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>Serializes a filter tree to the wire JSON filter value.</summary>
    public static string WriteFilter(FilterNode root)
        => JsonSerializer.Serialize(BuildNode(root), Options);

    /// <summary>Serializes groupBy (fields + nested aggregations) to the wire JSON groupBy value.</summary>
    public static string WriteGroupBy(IReadOnlyList<string> fields, AggregationSpec? aggregations)
    {
        var map = new Dictionary<string, object?> { ["fields"] = fields };
        if (aggregations is { HasAny: true })
            map["aggregations"] = BuildAggregations(aggregations);
        return JsonSerializer.Serialize(map, Options);
    }

    /// <summary>Serializes standalone aggregations (no groupBy) to the wire JSON aggregations value.</summary>
    public static string WriteAggregations(AggregationSpec aggregations)
        => JsonSerializer.Serialize(BuildAggregations(aggregations), Options);

    /// <summary>Serializes ordering to the wire JSON sort value (<c>{"fields":[{"field","direction"}]}</c>).</summary>
    public static string WriteOrderBy(IReadOnlyList<FilterOrder> orders)
    {
        var fields = orders
            .Select(o => new Dictionary<string, object?>
            {
                ["field"] = FieldName(o.Field),
                ["direction"] = o.Descending ? "desc" : "asc"
            })
            .ToList();
        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["fields"] = fields }, Options);
    }

    private static object BuildNode(FilterNode node) => node.Kind switch
    {
        FilterNodeKind.Condition => BuildCondition(node.Condition!),
        FilterNodeKind.And => new Dictionary<string, object?> { ["and"] = node.Children.Select(BuildNode).ToList() },
        FilterNodeKind.Or => new Dictionary<string, object?> { ["or"] = node.Children.Select(BuildNode).ToList() },
        FilterNodeKind.Not => new Dictionary<string, object?> { ["not"] = BuildNode(node.Children[0]) },
        _ => throw new InvalidOperationException($"Unsupported filter node kind '{node.Kind}'.")
    };

    private static Dictionary<string, object?> BuildCondition(FilterCondition condition)
    {
        var (opKey, opValue) = Operator(condition);
        var opMap = new Dictionary<string, object?> { [opKey] = opValue };

        if (condition.Field.Kind == FilterFieldKind.Column)
            return new Dictionary<string, object?> { [condition.Field.Name] = opMap };

        // Attribute: nest under "attributes", honoring dotted paths (a.b -> {"attributes":{"a":{"b":…}}}).
        object nested = opMap;
        var segments = condition.Field.Name.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
            nested = new Dictionary<string, object?> { [segments[i]] = nested };

        return new Dictionary<string, object?> { ["attributes"] = nested };
    }

    private static (string Key, object? Value) Operator(FilterCondition c) => c.Operator switch
    {
        FilterOperator.Eq => ("eq", c.Values[0]),
        FilterOperator.Ne => ("ne", c.Values[0]),
        FilterOperator.Gt => ("gt", c.Values[0]),
        FilterOperator.Ge => ("ge", c.Values[0]),
        FilterOperator.Lt => ("lt", c.Values[0]),
        FilterOperator.Le => ("le", c.Values[0]),
        FilterOperator.Like => ("like", c.Values[0]),
        FilterOperator.StartsWith => ("startswith", c.Values[0]),
        FilterOperator.EndsWith => ("endswith", c.Values[0]),
        FilterOperator.In => ("in", c.Values.ToArray()),
        FilterOperator.NotIn => ("nin", c.Values.ToArray()),
        FilterOperator.Between => ("between", new[] { c.Values[0], c.Values[1] }),
        FilterOperator.IsNull => ("isNull", c.Values[0]),
        _ => throw new InvalidOperationException($"Unsupported operator '{c.Operator}'.")
    };

    private static Dictionary<string, object?> BuildAggregations(AggregationSpec a)
    {
        var map = new Dictionary<string, object?>();
        if (a.Count) map["count"] = true;
        if (a.Sum is not null) map["sum"] = a.Sum;
        if (a.Avg is not null) map["avg"] = a.Avg;
        if (a.Min is not null) map["min"] = a.Min;
        if (a.Max is not null) map["max"] = a.Max;
        return map;
    }

    // Wire field name: columns bare (createdAt), attributes dotted with prefix (attributes.limitKey).
    private static string FieldName(FilterField field)
        => field.Kind == FilterFieldKind.Attribute ? $"attributes.{field.Name}" : field.Name;
}
