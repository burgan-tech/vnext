namespace BBT.Workflow.Filtering;

/// <summary>
/// Fluent builder for a single field's condition(s). Each method records one operator; multiple
/// operators on the same field compose as a logical AND. At least one operator must be declared.
/// </summary>
public sealed class FieldConditionBuilder
{
    private readonly List<(FilterOperator Operator, object?[] Values)> _conditions = new();

    /// <summary>Equal to.</summary>
    public FieldConditionBuilder Eq(object? value) => Add(FilterOperator.Eq, value);

    /// <summary>Not equal to.</summary>
    public FieldConditionBuilder Ne(object? value) => Add(FilterOperator.Ne, value);

    /// <summary>Greater than.</summary>
    public FieldConditionBuilder Gt(object? value) => Add(FilterOperator.Gt, value);

    /// <summary>Greater than or equal.</summary>
    public FieldConditionBuilder Ge(object? value) => Add(FilterOperator.Ge, value);

    /// <summary>Less than.</summary>
    public FieldConditionBuilder Lt(object? value) => Add(FilterOperator.Lt, value);

    /// <summary>Less than or equal.</summary>
    public FieldConditionBuilder Le(object? value) => Add(FilterOperator.Le, value);

    /// <summary>Contains substring (case-insensitive).</summary>
    public FieldConditionBuilder Like(string value) => Add(FilterOperator.Like, value);

    /// <summary>Starts with prefix (case-insensitive).</summary>
    public FieldConditionBuilder StartsWith(string value) => Add(FilterOperator.StartsWith, value);

    /// <summary>Ends with suffix (case-insensitive).</summary>
    public FieldConditionBuilder EndsWith(string value) => Add(FilterOperator.EndsWith, value);

    /// <summary>Value is in the given list.</summary>
    public FieldConditionBuilder In(params object[] values) => Add(FilterOperator.In, values);

    /// <summary>Value is not in the given list.</summary>
    public FieldConditionBuilder NotIn(params object[] values) => Add(FilterOperator.NotIn, values);

    /// <summary>Value is within the inclusive range [min, max].</summary>
    public FieldConditionBuilder Between(object min, object max) => Add(FilterOperator.Between, min, max);

    /// <summary>Null check (true = is null, false = is not null).</summary>
    public FieldConditionBuilder IsNull(bool isNull = true) => Add(FilterOperator.IsNull, isNull);

    /// <summary>
    /// JSON array containment: the array at this field path must contain at least one element
    /// matching the given partial object (wire <c>includes</c> operator, PostgreSQL
    /// <c>jsonb @&gt;</c>), e.g. <c>f.Includes(new { userId })</c>. List queries only — the
    /// single-resolve First()/Last() engine rejects it.
    /// </summary>
    public FieldConditionBuilder Includes(object partialMatch) => Add(FilterOperator.Includes, partialMatch);

    private FieldConditionBuilder Add(FilterOperator op, params object?[] values)
    {
        _conditions.Add((op, values));
        return this;
    }

    internal IReadOnlyList<FilterCondition> Build(FilterField field)
    {
        if (_conditions.Count == 0)
            throw new InvalidOperationException(
                "A field condition must declare at least one operator (e.g. Eq, Like, In).");

        return _conditions
            .Select(c => new FilterCondition(field, c.Operator, c.Values))
            .ToList();
    }
}

/// <summary>
/// Fluent, self-validating builder for an <see cref="InstanceFilter"/> — the reusable, code-first way
/// to describe which single workflow instance to resolve by filtering the instance store (the
/// alternative to hand-written filter JSON). Top-level <see cref="Where"/> clauses compose as a logical
/// AND; use <see cref="OrGroup"/>/<see cref="Not"/> for richer logic. Fields are instance columns
/// (<c>status</c>, <c>key</c>, <c>createdAt</c>, …) or JSON attribute paths prefixed with
/// <c>attributes.</c>. Terminate with <see cref="First"/>/<see cref="Last"/>.
/// <example>
/// <code>
/// InstanceQuery.Create()
///     .Where("attributes.name",    f => f.Eq(name))
///     .Where("attributes.surname", f => f.Eq(surname))
///     .Where("status",             f => f.Eq("Active"))
///     .OrderByDescending("createdAt")
///     .Last();
/// </code>
/// </example>
/// </summary>
public sealed class InstanceQuery
{
    private const string DefaultOrderField = "createdAt";
    private const string AttributesPrefix = "attributes.";

    private readonly List<FilterNode> _clauses = [];
    private FilterField _orderField = ParseField(DefaultOrderField);
    private bool _orderByDescending;
    private bool _orderExplicit;

    // List-query (Build) extras: groupBy fields + aggregations. Ignored by First()/Last().
    private readonly List<string> _groupByFields = [];
    private bool _count;
    private string? _sum;
    private string? _avg;
    private string? _min;
    private string? _max;

    private InstanceQuery()
    {
    }

    /// <summary>Starts a new query.</summary>
    public static InstanceQuery Create() => new();

    /// <summary>Adds a field condition. Multiple calls compose as a logical AND.</summary>
    public InstanceQuery Where(string field, Action<FieldConditionBuilder> configure)
    {
        if (configure is null)
            throw new ArgumentNullException(nameof(configure));

        var parsedField = ParseField(field);
        var fb = new FieldConditionBuilder();
        configure(fb);
        foreach (var condition in fb.Build(parsedField))
            _clauses.Add(FilterNode.Leaf(condition));

        return this;
    }

    /// <summary>Adds a nested OR group: at least one branch's conditions must match.</summary>
    public InstanceQuery OrGroup(params Action<InstanceQuery>[] branches)
    {
        if (branches is null || branches.Length == 0)
            throw new InvalidOperationException("OrGroup requires at least one branch.");

        var nodes = branches
            .Select(branch =>
            {
                var q = new InstanceQuery();
                branch(q);
                return q.BuildRoot();
            })
            .ToList();

        _clauses.Add(FilterNode.Any(nodes));
        return this;
    }

    /// <summary>Adds a nested NOT group: the inner conditions must NOT match.</summary>
    public InstanceQuery Not(Action<InstanceQuery> inner)
    {
        if (inner is null)
            throw new ArgumentNullException(nameof(inner));

        var q = new InstanceQuery();
        inner(q);
        _clauses.Add(FilterNode.Negate(q.BuildRoot()));
        return this;
    }

    /// <summary>Orders matches ascending by the given field.</summary>
    public InstanceQuery OrderBy(string field)
    {
        _orderField = ParseField(field);
        _orderByDescending = false;
        _orderExplicit = true;
        return this;
    }

    /// <summary>Orders matches descending by the given field.</summary>
    public InstanceQuery OrderByDescending(string field)
    {
        _orderField = ParseField(field);
        _orderByDescending = true;
        _orderExplicit = true;
        return this;
    }

    /// <summary>
    /// Groups results by one or more fields (list-query only; consumed by <see cref="Build"/>).
    /// Fields use the wire convention: columns bare, attributes dotted with prefix
    /// (e.g. <c>attributes.limitKey</c>).
    /// </summary>
    public InstanceQuery GroupBy(params string[] fields)
    {
        if (fields is null || fields.Length == 0)
            throw new InvalidOperationException("GroupBy requires at least one field.");
        foreach (var f in fields)
        {
            if (string.IsNullOrWhiteSpace(f))
                throw new InvalidOperationException("GroupBy field must be non-empty.");
            _groupByFields.Add(f.Trim());
        }
        return this;
    }

    /// <summary>Adds a COUNT aggregation (list-query only).</summary>
    public InstanceQuery Count()
    {
        _count = true;
        return this;
    }

    /// <summary>Adds a SUM aggregation over the given field (list-query only).</summary>
    public InstanceQuery Sum(string field)
    {
        _sum = RequireAggField(field);
        return this;
    }

    /// <summary>Adds an AVG aggregation over the given field (list-query only).</summary>
    public InstanceQuery Avg(string field)
    {
        _avg = RequireAggField(field);
        return this;
    }

    /// <summary>Adds a MIN aggregation over the given field (list-query only).</summary>
    public InstanceQuery Min(string field)
    {
        _min = RequireAggField(field);
        return this;
    }

    /// <summary>Adds a MAX aggregation over the given field (list-query only).</summary>
    public InstanceQuery Max(string field)
    {
        _max = RequireAggField(field);
        return this;
    }

    /// <summary>
    /// Builds a list-query specification (filter + ordering + optional groupBy/aggregations) that
    /// serializes to the wire strings the list endpoint expects. Filter may be empty (match-all).
    /// </summary>
    public InstanceQuerySpec Build()
    {
        IReadOnlyList<FilterOrder> orders = _orderExplicit
            ? [new FilterOrder(_orderField, _orderByDescending)]
            : [];

        var aggregations = HasAggregations()
            ? new AggregationSpec(_count, _sum, _avg, _min, _max)
            : null;

        return new InstanceQuerySpec(BuildRootOrNull(), orders, _groupByFields, aggregations);
    }

    /// <summary>Builds a filter that resolves the FIRST match under the effective ordering.</summary>
    public InstanceFilter First() => BuildSingle(InstanceSelection.First);

    /// <summary>Builds a filter that resolves the LAST match under the effective ordering.</summary>
    public InstanceFilter Last() => BuildSingle(InstanceSelection.Last);

    private InstanceFilter BuildSingle(InstanceSelection selection)
    {
        if (_groupByFields.Count > 0 || HasAggregations())
            throw new InvalidOperationException(
                "GroupBy/aggregations are list-query features — terminate with Build(), not First()/Last().");
        if (_clauses.Any(ContainsIncludes))
            throw new InvalidOperationException(
                "The Includes operator is a list-query feature — terminate with Build(), not First()/Last().");
        return new InstanceFilter(BuildRoot(), new FilterOrder(_orderField, _orderByDescending), selection);
    }

    // The single-resolve SQL engine has no jsonb containment support; fail at build time
    // (in the authoring script) rather than when the filter later executes.
    private static bool ContainsIncludes(FilterNode node) =>
        node.Kind == FilterNodeKind.Condition
            ? node.Condition!.Operator == FilterOperator.Includes
            : node.Children.Any(ContainsIncludes);

    private bool HasAggregations()
        => _count || _sum is not null || _avg is not null || _min is not null || _max is not null;

    private static string RequireAggField(string field)
    {
        if (string.IsNullOrWhiteSpace(field))
            throw new InvalidOperationException("Aggregation field must be non-empty.");
        return field.Trim();
    }

    private FilterNode BuildRoot()
    {
        if (_clauses.Count == 0)
            throw new InvalidOperationException(
                "InstanceQuery requires at least one Where/OrGroup/Not clause; an unfiltered match is not allowed.");

        return _clauses.Count == 1 ? _clauses[0] : FilterNode.All(_clauses);
    }

    // Like BuildRoot but allows an empty filter (null = match-all) — valid for list/groupBy queries.
    private FilterNode? BuildRootOrNull()
        => _clauses.Count switch
        {
            0 => null,
            1 => _clauses[0],
            _ => FilterNode.All(_clauses)
        };

    private static FilterField ParseField(string field)
    {
        if (string.IsNullOrWhiteSpace(field))
            throw new InvalidOperationException("Field name must be non-empty.");

        field = field.Trim();
        if (field.StartsWith(AttributesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var path = field.Substring(AttributesPrefix.Length).Trim();
            if (path.Length == 0)
                throw new InvalidOperationException("Attribute path must be non-empty after the 'attributes.' prefix.");
            return new FilterField(FilterFieldKind.Attribute, path);
        }

        return new FilterField(FilterFieldKind.Column, field);
    }
}
