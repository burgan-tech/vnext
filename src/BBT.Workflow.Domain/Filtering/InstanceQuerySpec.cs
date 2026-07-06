using System.Text.Json;

namespace BBT.Workflow.Filtering;

/// <summary>
/// A built list-query specification (filter + ordering + optional groupBy/aggregations) produced by
/// <see cref="InstanceQuery.Build"/>. Its purpose is to let domain scripts author list/report queries
/// with the fluent builder and then emit the exact wire strings the existing list endpoint expects —
/// replacing hand-concatenated GraphQL JSON. It does NOT run any query itself; it only serializes.
/// <example>
/// <code>
/// var q = InstanceQuery.Create()
///     .Where("attributes.absenceType", f => f.Eq(absenceType))
///     .OrGroup(
///         b => b.Where("currentState", f => f.Eq("complete")),
///         b => b.Where("currentState", f => f.Eq("active-leave")))
///     .GroupBy("attributes.limitKey")
///     .Sum("attributes.amount")
///     .Build();
///
/// serviceTask.SetQueryString(q.ToQueryString(page: 1, pageSize: 100));
/// // or compose manually: $"filter={q.ToFilterJson()}&groupBy={q.ToGroupByJson()}"
/// </code>
/// </example>
/// </summary>
public sealed class InstanceQuerySpec
{
    internal InstanceQuerySpec(
        FilterNode? filter,
        IReadOnlyList<FilterOrder> orders,
        IReadOnlyList<string> groupByFields,
        AggregationSpec? aggregations)
    {
        Filter = filter;
        Orders = orders;
        GroupByFields = groupByFields;
        Aggregations = aggregations;
    }

    /// <summary>Filter expression tree (or null for match-all).</summary>
    public FilterNode? Filter { get; }

    /// <summary>Ordering fields (may be empty).</summary>
    public IReadOnlyList<FilterOrder> Orders { get; }

    /// <summary>GroupBy fields (may be empty). When set, aggregations are nested under groupBy.</summary>
    public IReadOnlyList<string> GroupByFields { get; }

    /// <summary>Aggregations (or null).</summary>
    public AggregationSpec? Aggregations { get; }

    /// <summary>The <c>filter</c> query value as GraphQL wire JSON, or null when there is no filter.</summary>
    public string? ToFilterJson()
        => Filter is null ? null : GraphQlWireWriter.WriteFilter(Filter);

    /// <summary>The <c>groupBy</c> query value as wire JSON (fields + nested aggregations), or null.</summary>
    public string? ToGroupByJson()
        => GroupByFields.Count == 0 ? null : GraphQlWireWriter.WriteGroupBy(GroupByFields, Aggregations);

    /// <summary>The standalone <c>aggregations</c> query value (only when there is no groupBy), or null.</summary>
    public string? ToAggregationsJson()
        => GroupByFields.Count == 0 && Aggregations is { HasAny: true }
            ? GraphQlWireWriter.WriteAggregations(Aggregations)
            : null;

    /// <summary>The <c>sort</c> query value as wire JSON, or null when no ordering was specified.</summary>
    public string? ToSortJson()
        => Orders.Count == 0 ? null : GraphQlWireWriter.WriteOrderBy(Orders);

    /// <summary>
    /// Convenience: builds the full query string (<c>page</c>, <c>pageSize</c>, and whichever of
    /// <c>filter</c>/<c>groupBy</c>/<c>aggregations</c>/<c>sort</c> are present). Values are URL-encoded.
    /// </summary>
    public string ToQueryString(int page = 1, int pageSize = 100)
    {
        var parts = new List<string>
        {
            $"page={page}",
            $"pageSize={pageSize}"
        };

        void Add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                parts.Add($"{name}={Uri.EscapeDataString(value)}");
        }

        Add("filter", ToFilterJson());
        Add("groupBy", ToGroupByJson());
        Add("aggregations", ToAggregationsJson());
        Add("sort", ToSortJson());

        return string.Join("&", parts);
    }
}
