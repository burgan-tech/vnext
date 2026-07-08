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

    /// <summary>
    /// Builds the ORDER BY clause SQL (e.g. <c>s."CreatedAt" DESC</c>) for the given effective direction.
    /// Attribute paths keep the final segment as jsonb (<c>-&gt;</c> instead of <c>-&gt;&gt;</c>) so PostgreSQL
    /// applies native jsonb type ordering — numbers sort numerically (9 &lt; 20 &lt; 100), not as text
    /// ("100" &lt; "20" &lt; "9") — which is what First/Last selection depends on.
    /// </summary>
    public static string BuildOrderBy(FilterField field, bool descending)
        => $"{Accessor(field, textFinalSegment: false)} {(descending ? "DESC" : "ASC")}";

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
            {
                var value = Single(condition);
                var cast = StrongCast(value);
                return cast is null
                    ? $"{TextAccessor(accessor, isAttribute)} = {Param(AsText(value))}"
                    : $"({accessor}){cast} = {Param(value)}";
            }
            case FilterOperator.Ne:
            {
                var value = Single(condition);
                var cast = StrongCast(value);
                return cast is null
                    ? $"{TextAccessor(accessor, isAttribute)} <> {Param(AsText(value))}"
                    : $"({accessor}){cast} <> {Param(value)}";
            }
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
            {
                var cast = StrongCast(condition.Values.FirstOrDefault());
                return cast is null
                    ? $"{TextAccessor(accessor, isAttribute)} IN ({ParamList(condition)})"
                    : $"({accessor}){cast} IN ({ParamList(condition, asText: false)})";
            }
            case FilterOperator.NotIn:
            {
                var cast = StrongCast(condition.Values.FirstOrDefault());
                return cast is null
                    ? $"{TextAccessor(accessor, isAttribute)} NOT IN ({ParamList(condition)})"
                    : $"({accessor}){cast} NOT IN ({ParamList(condition, asText: false)})";
            }
            case FilterOperator.IsNull:
                var isNull = Single(condition) is bool b && b;
                return $"{TextAccessor(accessor, isAttribute)} IS {(isNull ? "NULL" : "NOT NULL")}";
            default:
                throw new InvalidOperationException($"Unsupported filter operator '{condition.Operator}'.");
        }
    }

    /// <summary>
    /// Column/JSON accessor (untyped). Columns: <c>s."Col"</c>; attributes: <c>d."Data" -&gt; 'a' -&gt;&gt; 'b'</c>.
    /// With <paramref name="textFinalSegment"/> false, the final attribute segment stays jsonb
    /// (<c>-&gt;</c>) — used by ORDER BY for native jsonb type ordering.
    /// </summary>
    private static string Accessor(FilterField field, bool textFinalSegment = true)
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
            var op = textFinalSegment && i == segments.Length - 1 ? " ->> " : " -> ";
            sb.Append(op).Append('\'').Append(EscapeSql(segments[i])).Append('\'');
        }
        return sb.ToString();
    }

    // For text-based comparisons an attribute accessor already yields text (->>); columns are cast to text.
    private static string TextAccessor(string accessor, bool isAttribute)
        => isAttribute ? accessor : $"{accessor}::text";

    // Equality/membership comparisons run in a typed domain only when the operand is a strongly-typed
    // .NET number or date (Eq(30) must match a stored 30.0). String operands — even numeric- or
    // date-looking ones such as customer numbers or date-formatted codes — always compare as text, so
    // rows holding non-conforming values can never fail a cast at runtime. Range operators differ
    // deliberately: TypedAccessor probes strings there, because date/number range bounds arrive as
    // ISO-formatted strings from scripts.
    private static string? StrongCast(object? value) => value switch
    {
        DateTime or DateTimeOffset => "::timestamptz",
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => "::numeric",
        _ => null
    };

    // For range comparisons, cast the (text) accessor to the type implied by the operand. Operands
    // that are neither numeric nor date-like (e.g. an alphabetical range: name > 'M') get no cast —
    // a ::numeric default would make PostgreSQL reject the value at runtime; plain text comparison
    // is the correct semantic there.
    private static string TypedAccessor(string accessor, FilterCondition condition, bool isAttribute)
    {
        var probe = condition.Values.FirstOrDefault();
        var cast = probe switch
        {
            DateTime or DateTimeOffset => "::timestamptz",
            sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => "::numeric",
            string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) => "::timestamptz",
            string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out _) => "::numeric",
            _ => null
        };

        // Parenthesize before casting: '::' binds tighter than the JSON '->>' operator, so
        // "Data" ->> 'age'::numeric would cast the key 'age' instead of the extracted value.
        // (isAttribute is irrelevant to the cast; the parens are what matter.)
        return cast is null ? accessor : $"({accessor}){cast}";
    }

    private static object? Single(FilterCondition condition)
    {
        RequireCount(condition, 1, condition.Operator.ToString().ToLowerInvariant());
        return condition.Values[0];
    }

    private string ParamList(FilterCondition condition, bool asText = true)
    {
        if (condition.Values.Count == 0)
            throw new InvalidOperationException($"Operator '{condition.Operator}' requires at least one value.");
        return string.Join(", ", condition.Values.Select(v => Param(asText ? AsText(v) : v)));
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
