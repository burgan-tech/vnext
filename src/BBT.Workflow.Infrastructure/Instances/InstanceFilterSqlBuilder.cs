using System.Globalization;
using System.Text;
using BBT.Workflow.Filtering;

namespace BBT.Workflow.Infrastructure.Instances;

/// <summary>
/// Translates an <see cref="InstanceFilter"/> into a parameterized SQL WHERE clause and ORDER BY clause
/// for the instance filter engine. Instance columns resolve to typed columns on <c>Instances s</c>;
/// attribute paths resolve to JSONB access on the latest <c>InstancesData d</c> row
/// (<c>d."Data" -&gt; 'a' -&gt;&gt; 'b'</c>). All operands are emitted as positional parameters
/// (<c>{0}</c>, <c>{1}</c>, …) — never string-concatenated — so the clause is injection-safe.
/// This is the new, self-contained translator; it does not use the legacy GraphQL filter services.
/// </summary>
internal sealed class InstanceFilterSqlBuilder
{
    // Whitelisted instance columns (input name -> DB column). Case-insensitive on input.
    private static readonly IReadOnlyDictionary<string, string> ColumnMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = "Id",
            ["key"] = "Key",
            ["flow"] = "Flow",
            ["status"] = "Status",
            ["state"] = "CurrentState",
            ["currentstate"] = "CurrentState",
            ["effectivestate"] = "EffectiveState",
            ["effectivestatetype"] = "EffectiveStateType",
            ["effectivestatesubtype"] = "EffectiveStateSubType",
            ["stage"] = "Stage",
            ["createdat"] = "CreatedAt",
            ["modifiedat"] = "ModifiedAt",
            ["completedat"] = "CompletedAt"
        };

    private readonly List<object?> _parameters = new();

    /// <summary>The positional parameter values, in order, for the emitted <c>{n}</c> placeholders.</summary>
    public IReadOnlyList<object?> Parameters => _parameters;

    /// <summary>Builds the WHERE clause SQL for the given filter tree.</summary>
    public string BuildWhere(FilterNode root) => BuildNode(root);

    /// <summary>Builds the ORDER BY clause SQL (e.g. <c>s."CreatedAt" DESC</c>) for the given effective direction.</summary>
    public static string BuildOrderBy(FilterField field, bool descending)
        => $"{Accessor(field)} {(descending ? "DESC" : "ASC")}";

    private string BuildNode(FilterNode node)
    {
        return node.Kind switch
        {
            FilterNodeKind.Condition => BuildCondition(node.Condition!),
            FilterNodeKind.And => "(" + string.Join(" AND ", node.Children.Select(BuildNode)) + ")",
            FilterNodeKind.Or => "(" + string.Join(" OR ", node.Children.Select(BuildNode)) + ")",
            FilterNodeKind.Not => "NOT (" + BuildNode(node.Children[0]) + ")",
            _ => throw new InvalidOperationException($"Unsupported filter node kind '{node.Kind}'.")
        };
    }

    private string BuildCondition(FilterCondition condition)
    {
        var accessor = Accessor(condition.Field);
        var isAttribute = condition.Field.Kind == FilterFieldKind.Attribute;

        switch (condition.Operator)
        {
            case FilterOperator.Eq:
                return $"{TextAccessor(accessor, isAttribute)} = {Param(AsText(Single(condition)))}";
            case FilterOperator.Ne:
                return $"{TextAccessor(accessor, isAttribute)} <> {Param(AsText(Single(condition)))}";
            case FilterOperator.Like:
                return $"{TextAccessor(accessor, isAttribute)} ILIKE {Param("%" + AsText(Single(condition)) + "%")}";
            case FilterOperator.StartsWith:
                return $"{TextAccessor(accessor, isAttribute)} ILIKE {Param(AsText(Single(condition)) + "%")}";
            case FilterOperator.EndsWith:
                return $"{TextAccessor(accessor, isAttribute)} ILIKE {Param("%" + AsText(Single(condition)))}";
            case FilterOperator.Gt:
                return $"{TypedAccessor(accessor, condition, isAttribute)} > {Param(Single(condition))}";
            case FilterOperator.Ge:
                return $"{TypedAccessor(accessor, condition, isAttribute)} >= {Param(Single(condition))}";
            case FilterOperator.Lt:
                return $"{TypedAccessor(accessor, condition, isAttribute)} < {Param(Single(condition))}";
            case FilterOperator.Le:
                return $"{TypedAccessor(accessor, condition, isAttribute)} <= {Param(Single(condition))}";
            case FilterOperator.Between:
                RequireCount(condition, 2, "between");
                return $"{TypedAccessor(accessor, condition, isAttribute)} BETWEEN {Param(condition.Values[0])} AND {Param(condition.Values[1])}";
            case FilterOperator.In:
                return $"{TextAccessor(accessor, isAttribute)} IN ({ParamList(condition)})";
            case FilterOperator.NotIn:
                return $"{TextAccessor(accessor, isAttribute)} NOT IN ({ParamList(condition)})";
            case FilterOperator.IsNull:
                var isNull = Single(condition) is bool b && b;
                return $"{TextAccessor(accessor, isAttribute)} IS {(isNull ? "NULL" : "NOT NULL")}";
            default:
                throw new InvalidOperationException($"Unsupported filter operator '{condition.Operator}'.");
        }
    }

    /// <summary>Column/JSON accessor (untyped). Columns: <c>s."Col"</c>; attributes: <c>d."Data" -&gt; 'a' -&gt;&gt; 'b'</c>.</summary>
    private static string Accessor(FilterField field)
    {
        if (field.Kind == FilterFieldKind.Column)
        {
            if (!ColumnMap.TryGetValue(field.Name, out var column))
                throw new InvalidOperationException($"Unknown or non-filterable instance column '{field.Name}'.");
            return $"s.\"{column}\"";
        }

        var segments = field.Name.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            throw new InvalidOperationException("Attribute path must be non-empty.");

        // Instance-data attributes are stored top-level in the "Data" JSONB column
        // (e.g. {"name":...,"surname":...}). A single segment => Data ->> 'name';
        // a nested path a.b => Data -> 'a' ->> 'b'.
        var sb = new StringBuilder("d.\"Data\"");
        for (var i = 0; i < segments.Length; i++)
        {
            var op = i == segments.Length - 1 ? " ->> " : " -> ";
            sb.Append(op).Append('\'').Append(EscapeSql(segments[i])).Append('\'');
        }
        return sb.ToString();
    }

    // For text-based comparisons an attribute accessor already yields text (->>); columns are cast to text.
    private static string TextAccessor(string accessor, bool isAttribute)
        => isAttribute ? accessor : $"{accessor}::text";

    // For numeric/date comparisons, cast the (text) accessor to the appropriate type based on the operand.
    private static string TypedAccessor(string accessor, FilterCondition condition, bool isAttribute)
    {
        var probe = condition.Values.FirstOrDefault();
        var cast = probe switch
        {
            DateTime or DateTimeOffset => "::timestamptz",
            sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => "::numeric",
            string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) => "::timestamptz",
            string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out _) => "::numeric",
            _ => "::numeric"
        };

        // Attribute accessor is already text (->>); a plain column needs ->text semantics only for JSON,
        // so columns are cast directly. Both then cast to the comparison type.
        return $"{(isAttribute ? accessor : accessor)}{cast}";
    }

    private static object? Single(FilterCondition condition)
    {
        RequireCount(condition, 1, condition.Operator.ToString().ToLowerInvariant());
        return condition.Values[0];
    }

    private string ParamList(FilterCondition condition)
    {
        if (condition.Values.Count == 0)
            throw new InvalidOperationException($"Operator '{condition.Operator}' requires at least one value.");
        return string.Join(", ", condition.Values.Select(v => Param(AsText(v))));
    }

    private string Param(object? value)
    {
        _parameters.Add(value);
        return "{" + (_parameters.Count - 1) + "}";
    }

    private static string? AsText(object? value) => value?.ToString();

    private static void RequireCount(FilterCondition condition, int expected, string opName)
    {
        if (condition.Values.Count != expected)
            throw new InvalidOperationException(
                $"Operator '{opName}' requires exactly {expected} value(s) but got {condition.Values.Count}.");
    }

    private static string EscapeSql(string value) => value.Replace("'", "''");
}
