namespace BBT.Workflow.Filtering;

/// <summary>
/// Comparison/matching operators supported by the instance filter engine.
/// </summary>
public enum FilterOperator
{
    /// <summary>Equal to.</summary>
    Eq,
    /// <summary>Not equal to.</summary>
    Ne,
    /// <summary>Greater than.</summary>
    Gt,
    /// <summary>Greater than or equal.</summary>
    Ge,
    /// <summary>Less than.</summary>
    Lt,
    /// <summary>Less than or equal.</summary>
    Le,
    /// <summary>Contains substring (case-insensitive).</summary>
    Like,
    /// <summary>Starts with prefix (case-insensitive).</summary>
    StartsWith,
    /// <summary>Ends with suffix (case-insensitive).</summary>
    EndsWith,
    /// <summary>Value is in the given list.</summary>
    In,
    /// <summary>Value is not in the given list.</summary>
    NotIn,
    /// <summary>Value is within the inclusive range [min, max].</summary>
    Between,
    /// <summary>Null / not-null check (Value is a bool: true = is null).</summary>
    IsNull,
    /// <summary>
    /// JSON array containment: the array at the field path contains at least one element matching
    /// the partial object (wire <c>includes</c> operator, PostgreSQL <c>jsonb @&gt;</c>).
    /// List queries only — the single-resolve First/Last SQL engine does not support it.
    /// </summary>
    Includes
}

/// <summary>
/// Whether a filtered field is a first-class instance column or a path inside the instance-data JSON.
/// </summary>
public enum FilterFieldKind
{
    /// <summary>A column on the Instances row (e.g. <c>status</c>, <c>key</c>, <c>createdAt</c>).</summary>
    Column,
    /// <summary>A path inside the instance-data JSON attributes (e.g. <c>name</c>, <c>address.city</c>).</summary>
    Attribute
}

/// <summary>
/// Identifies a filtered field: either an instance column or a JSON attribute path.
/// </summary>
public sealed class FilterField(FilterFieldKind kind, string name)
{
    /// <summary>Column vs attribute.</summary>
    public FilterFieldKind Kind { get; } = kind;

    /// <summary>Column name, or attribute path (dot-separated, without the <c>attributes.</c> prefix).</summary>
    public string Name { get; } = name;
}

/// <summary>
/// A single leaf condition: a field, an operator, and its operand(s).
/// </summary>
public sealed class FilterCondition(FilterField field, FilterOperator @operator, IReadOnlyList<object?> values)
{
    /// <summary>The field being filtered.</summary>
    public FilterField Field { get; } = field;

    /// <summary>The operator applied.</summary>
    public FilterOperator Operator { get; } = @operator;

    /// <summary>
    /// Operand values. Single-value operators use one entry; <see cref="FilterOperator.In"/>/<see cref="FilterOperator.NotIn"/>
    /// carry the list; <see cref="FilterOperator.Between"/> carries exactly two; <see cref="FilterOperator.IsNull"/> carries one bool.
    /// </summary>
    public IReadOnlyList<object?> Values { get; } = values;
}

/// <summary>How the children of a <see cref="FilterNode"/> combine.</summary>
public enum FilterNodeKind
{
    /// <summary>Leaf condition.</summary>
    Condition,
    /// <summary>All children must match.</summary>
    And,
    /// <summary>At least one child must match.</summary>
    Or,
    /// <summary>The single child must NOT match.</summary>
    Not
}

/// <summary>
/// A node in the filter expression tree — either a leaf <see cref="FilterCondition"/> or a logical
/// grouping (And/Or/Not) of child nodes. Immutable.
/// </summary>
public sealed class FilterNode
{
    private FilterNode(FilterNodeKind kind, FilterCondition? condition, IReadOnlyList<FilterNode>? children)
    {
        Kind = kind;
        Condition = condition;
        Children = children ?? [];
    }

    /// <summary>The node kind.</summary>
    public FilterNodeKind Kind { get; }

    /// <summary>The leaf condition (only when <see cref="Kind"/> is <see cref="FilterNodeKind.Condition"/>).</summary>
    public FilterCondition? Condition { get; }

    /// <summary>Child nodes (for And/Or/Not).</summary>
    public IReadOnlyList<FilterNode> Children { get; }

    /// <summary>Creates a leaf condition node.</summary>
    public static FilterNode Leaf(FilterCondition condition) =>
        new(FilterNodeKind.Condition, condition, null);

    /// <summary>Creates an AND grouping.</summary>
    public static FilterNode All(IReadOnlyList<FilterNode> children) =>
        new(FilterNodeKind.And, null, children);

    /// <summary>Creates an OR grouping.</summary>
    public static FilterNode Any(IReadOnlyList<FilterNode> children) =>
        new(FilterNodeKind.Or, null, children);

    /// <summary>Creates a NOT grouping around a single child.</summary>
    public static FilterNode Negate(FilterNode child) =>
        new(FilterNodeKind.Not, null, [child]);
}

/// <summary>Ordering applied before First/Last selection.</summary>
public sealed class FilterOrder(FilterField field, bool descending)
{
    /// <summary>The field to order by.</summary>
    public FilterField Field { get; } = field;

    /// <summary>Descending when true, ascending otherwise.</summary>
    public bool Descending { get; } = descending;
}

/// <summary>Which single match to take when the filter matches more than one instance.</summary>
public enum InstanceSelection
{
    /// <summary>Take the first row under the effective ordering.</summary>
    First,
    /// <summary>Take the last row under the effective ordering.</summary>
    Last
}

/// <summary>
/// Immutable, engine-agnostic description of "which single instance" to resolve: a filter expression
/// tree, an ordering, and a First/Last selection. Produced by the fluent
/// <see cref="InstanceQuery"/> builder and executed by the instance filter engine.
/// This is the new, self-contained filter model — independent of the legacy GraphQL filter types — and
/// is intended to be reused wherever instances are queried by data (event correlation, tasks, functions).
/// </summary>
public sealed class InstanceFilter(FilterNode root, FilterOrder order, InstanceSelection selection)
{
    /// <summary>Root of the filter expression tree.</summary>
    public FilterNode Root { get; } = root;

    /// <summary>Ordering applied before selection.</summary>
    public FilterOrder Order { get; } = order;

    /// <summary>First or last match.</summary>
    public InstanceSelection Selection { get; } = selection;
}
