using System.Text.Json;
using BBT.Aether.Results;

namespace BBT.Workflow.Definitions.RowSecurity;

/// <summary>
/// A row-security rule filter parsed into a typed tree whose leaf values may still be parameters.
/// </summary>
/// <param name="Root">The root node.</param>
/// <remarks>
/// Parsed from the raw rule JSON, not from <c>GraphQLFilterNode</c>: a form-C parameter object
/// (<c>{"in":{"$param":...,"split":","}}</c>) cannot bind to <c>FieldCondition.In</c> (<c>object[]</c>),
/// and element-wise parameters inside an array must survive until request time.
/// </remarks>
public sealed record RowFilterTemplate(RowTemplateNode Root);

/// <summary>
/// A node of a <see cref="RowFilterTemplate"/>: <see cref="RowTemplateAnd"/>, <see cref="RowTemplateOr"/>,
/// <see cref="RowTemplateNot"/> or <see cref="RowTemplateLeaf"/>.
/// </summary>
public abstract record RowTemplateNode;

/// <summary>All children must match.</summary>
/// <param name="Children">The child nodes (at least one).</param>
public sealed record RowTemplateAnd(IReadOnlyList<RowTemplateNode> Children) : RowTemplateNode
{
    /// <inheritdoc />
    public bool Equals(RowTemplateAnd? other) =>
        other is not null && Children.SequenceEqual(other.Children);

    /// <inheritdoc />
    public override int GetHashCode() => Children.Count;
}

/// <summary>At least one child must match.</summary>
/// <param name="Children">The child nodes (at least one).</param>
public sealed record RowTemplateOr(IReadOnlyList<RowTemplateNode> Children) : RowTemplateNode
{
    /// <inheritdoc />
    public bool Equals(RowTemplateOr? other) =>
        other is not null && Children.SequenceEqual(other.Children);

    /// <inheritdoc />
    public override int GetHashCode() => Children.Count;
}

/// <summary>The child must not match.</summary>
/// <param name="Child">The negated node.</param>
public sealed record RowTemplateNot(RowTemplateNode Child) : RowTemplateNode;

/// <summary>
/// One field condition: <paramref name="Field"/> <paramref name="Operator"/> <paramref name="Value"/>.
/// </summary>
/// <param name="Field">The authored field name (instance column or data path).</param>
/// <param name="IsInstanceColumn">True when the list engine treats <paramref name="Field"/> as an instance column.</param>
/// <param name="Operator">The canonical operator name (<c>eq</c>, <c>in</c>, <c>isNull</c>, ...).</param>
/// <param name="Value">The operand.</param>
public sealed record RowTemplateLeaf(string Field, bool IsInstanceColumn, string Operator, RowTemplateValue Value)
    : RowTemplateNode;

/// <summary>
/// The operand of a <see cref="RowTemplateLeaf"/>.
/// </summary>
public abstract record RowTemplateValue
{
    private RowTemplateValue()
    {
    }

    /// <summary>A literal JSON value (a detached clone).</summary>
    /// <param name="Value">The literal.</param>
    public sealed record Literal(JsonElement Value) : RowTemplateValue
    {
        /// <inheritdoc />
        public bool Equals(Literal? other) =>
            other is not null && JsonElement.DeepEquals(Value, other.Value);

        /// <inheritdoc />
        public override int GetHashCode() => Value.ValueKind.GetHashCode();
    }

    /// <summary>A single value read from a parameter at request time.</summary>
    /// <param name="Path">The parameter path.</param>
    public sealed record Parameter(RowParameterPath Path) : RowTemplateValue;

    /// <summary>An array operand whose elements are resolved one by one (<c>in</c>, <c>nin</c>, <c>between</c>).</summary>
    /// <param name="Items">The elements, each a <see cref="Literal"/> or a <see cref="Parameter"/>.</param>
    public sealed record Elements(IReadOnlyList<RowTemplateValue> Items) : RowTemplateValue
    {
        /// <inheritdoc />
        public bool Equals(Elements? other) =>
            other is not null && Items.SequenceEqual(other.Items);

        /// <inheritdoc />
        public override int GetHashCode() => Items.Count;
    }

    /// <summary>
    /// An array operand read from one parameter (form C, <c>{"$param":...,"split":","}</c>); the value is
    /// split on <paramref name="Separator"/>, or used whole when it is null.
    /// </summary>
    /// <param name="Path">The parameter path.</param>
    /// <param name="Separator">The split separator, or null.</param>
    public sealed record SplitParameter(RowParameterPath Path, string? Separator) : RowTemplateValue;
}

/// <summary>
/// Parses a raw row-security rule filter into a <see cref="RowFilterTemplate"/>.
/// </summary>
/// <remarks>
/// <para>Shape: the instance-filter JSON — a node is <c>and</c> / <c>or</c> (non-empty arrays),
/// <c>not</c> (object) or field conditions (under <c>attributes</c>, or at the node root as the list
/// engine also accepts). A node carries one logical kind or field conditions, never both. Several
/// fields or operators in one node become an <see cref="RowTemplateAnd"/>. Nested field objects are
/// not accepted; write a dotted field name instead.</para>
/// <para>Values: a string starting with <see cref="RowParameterPath.Prefix"/> is a parameter (forms A/B);
/// <c>{"$param":...,"split"?:...}</c> is form C. <c>$InstanceStarter</c> is accepted only on
/// <c>createdBy</c> and <c>$InstanceBehalfOfStarter</c> only on <c>createdByBehalfOf</c>.</para>
/// <para>Error codes: <see cref="RowSecurityErrorCodes.E06"/>, <see cref="RowSecurityErrorCodes.E07"/>,
/// <see cref="RowSecurityErrorCodes.E09"/>, <see cref="RowSecurityErrorCodes.E10"/>,
/// <see cref="RowSecurityErrorCodes.E11"/>, <see cref="RowSecurityErrorCodes.E13"/>.</para>
/// </remarks>
public static class RowFilterTemplateParser
{
    private const string ParamKey = "$param";
    private const string SplitKey = "split";
    private const string CreatedByField = "createdBy";
    private const string CreatedByBehalfOfField = "createdByBehalfOf";

    private static readonly string[] ScalarOperators =
        ["eq", "ne", "gt", "ge", "lt", "le", "like", "match", "startswith", "endswith"];

    private static readonly string[] ArrayOperators = ["in", "nin"];

    private static readonly string[] AllOperators =
        [.. ScalarOperators, .. ArrayOperators, "between", "isNull", "includes"];

    private static readonly string[] QueryShapeKeys = ["sort", "orderBy", "groupBy", "aggregations"];

    /// <summary>
    /// Parses <paramref name="filter"/>.
    /// </summary>
    /// <param name="filter">The rule's raw <c>filter</c> JSON.</param>
    /// <returns>The template, or a validation error carrying an RS-E code.</returns>
    public static Result<RowFilterTemplate> Parse(JsonElement filter)
    {
        return TryParseNode(filter, "filter", out var root, out var error)
            ? Result<RowFilterTemplate>.Ok(new RowFilterTemplate(root!))
            : Result<RowFilterTemplate>.Fail(error!.Value);
    }

    private static bool TryParseNode(JsonElement element, string location, out RowTemplateNode? node, out Error? error)
    {
        node = null;
        if (element.ValueKind != JsonValueKind.Object)
            return Fail(RowSecurityErrorCodes.E09, "A filter node must be a JSON object.", location, out error);

        foreach (var property in element.EnumerateObject())
        {
            if (QueryShapeKeys.Any(k => k.Equals(property.Name, StringComparison.OrdinalIgnoreCase)))
                return Fail(RowSecurityErrorCodes.E13,
                    "sort, orderBy, groupBy and aggregations are not allowed inside a row-security filter.",
                    location, out error);
        }

        var logicalNodes = new List<RowTemplateNode>();
        var leaves = new List<RowTemplateNode>();

        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name;
            var childLocation = $"{location}.{name}";

            if (name.Equals("and", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("or", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseNodeArray(property.Value, childLocation, out var children, out error))
                    return false;
                logicalNodes.Add(name.Equals("and", StringComparison.OrdinalIgnoreCase)
                    ? new RowTemplateAnd(children!)
                    : new RowTemplateOr(children!));
            }
            else if (name.Equals("not", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseNode(property.Value, childLocation, out var child, out error))
                    return false;
                logicalNodes.Add(new RowTemplateNot(child!));
            }
            else if (name.Equals("attributes", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.Object || !property.Value.EnumerateObject().Any())
                    return Fail(RowSecurityErrorCodes.E09, "attributes must be a non-empty object.", childLocation, out error);

                foreach (var field in property.Value.EnumerateObject())
                {
                    if (!TryParseField(field.Name, field.Value, $"{childLocation}.{field.Name}", leaves, out error))
                        return false;
                }
            }
            else if (property.Value.ValueKind == JsonValueKind.Object)
            {
                // Root-level field condition, as the list engine also accepts ({"key":{"eq":"1"}}).
                if (!TryParseField(name, property.Value, childLocation, leaves, out error))
                    return false;
            }
            else
            {
                return Fail(RowSecurityErrorCodes.E09, "Unknown filter node member.", childLocation, out error);
            }
        }

        var kinds = logicalNodes.Count + (leaves.Count > 0 ? 1 : 0);
        if (kinds != 1)
        {
            return Fail(RowSecurityErrorCodes.E09,
                kinds == 0
                    ? "A filter node must declare and, or, not or field conditions."
                    : "A filter node must declare exactly one of and, or, not or field conditions.",
                location, out error);
        }

        error = null;
        node = logicalNodes.Count == 1 ? logicalNodes[0]
            : leaves.Count == 1 ? leaves[0]
            : new RowTemplateAnd(leaves);
        return true;
    }

    private static bool TryParseNodeArray(JsonElement element, string location,
        out List<RowTemplateNode>? children, out Error? error)
    {
        children = null;
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
            return Fail(RowSecurityErrorCodes.E09, "and / or must be a non-empty array.", location, out error);

        var list = new List<RowTemplateNode>();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (!TryParseNode(item, $"{location}[{index++}]", out var child, out error))
                return false;
            list.Add(child!);
        }

        children = list;
        error = null;
        return true;
    }

    private static bool TryParseField(string field, JsonElement condition, string location,
        List<RowTemplateNode> leaves, out Error? error)
    {
        if (string.IsNullOrWhiteSpace(field))
            return Fail(RowSecurityErrorCodes.E09, "A field name must not be empty.", location, out error);

        if (condition.ValueKind != JsonValueKind.Object || !condition.EnumerateObject().Any())
            return Fail(RowSecurityErrorCodes.E09, "A field condition must be a non-empty object.", location, out error);

        var isInstanceColumn = InstanceFieldDiscriminator.IsInstanceColumn(field);

        foreach (var operand in condition.EnumerateObject())
        {
            var operatorLocation = $"{location}.{operand.Name}";
            var op = AllOperators.FirstOrDefault(o => o.Equals(operand.Name, StringComparison.OrdinalIgnoreCase));
            if (op is null)
                return Fail(RowSecurityErrorCodes.E09, "Unknown filter operator.", operatorLocation, out error);

            if (!TryParseOperand(field, op, operand.Value, operatorLocation, out var value, out error))
                return false;

            leaves.Add(new RowTemplateLeaf(field, isInstanceColumn, op, value!));
        }

        error = null;
        return true;
    }

    private static bool TryParseOperand(string field, string op, JsonElement element, string location,
        out RowTemplateValue? value, out Error? error)
    {
        value = null;

        if (ScalarOperators.Contains(op))
            return TryParseScalar(field, element, location, out value, out error);

        if (ArrayOperators.Contains(op))
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (!TryParseParamObject(element, location, out var path, out var separator, out error))
                    return false;
                value = new RowTemplateValue.SplitParameter(path!, separator);
                return true;
            }

            if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
                return Fail(RowSecurityErrorCodes.E09,
                    "in / nin take a non-empty array or a $param object.", location, out error);

            return TryParseElements(field, element, location, out value, out error);
        }

        switch (op)
        {
            case "between":
                if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 2)
                    return Fail(RowSecurityErrorCodes.E10, "between takes an array of exactly two values.", location, out error);
                return TryParseElements(field, element, location, out value, out error);

            case "isNull":
                if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return Fail(RowSecurityErrorCodes.E09, "isNull takes a boolean.", location, out error);
                value = new RowTemplateValue.Literal(element.Clone());
                error = null;
                return true;

            default: // includes
                if (element.ValueKind != JsonValueKind.Object)
                    return Fail(RowSecurityErrorCodes.E09, "includes takes a JSON object.", location, out error);
                value = new RowTemplateValue.Literal(element.Clone());
                error = null;
                return true;
        }
    }

    private static bool TryParseElements(string field, JsonElement array, string location,
        out RowTemplateValue? value, out Error? error)
    {
        value = null;
        var items = new List<RowTemplateValue>();
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (!TryParseScalar(field, item, $"{location}[{index++}]", out var element, out error))
                return false;
            items.Add(element!);
        }

        value = new RowTemplateValue.Elements(items);
        error = null;
        return true;
    }

    private static bool TryParseScalar(string field, JsonElement element, string location,
        out RowTemplateValue? value, out Error? error)
    {
        value = null;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return TryParseString(field, element, location, out value, out error);

            case JsonValueKind.Object:
                if (!TryParseParamObject(element, location, out var path, out var separator, out error))
                    return false;
                if (separator is not null)
                    return Fail(RowSecurityErrorCodes.E11,
                        "split is allowed only on an in / nin operand.", location, out error);
                value = new RowTemplateValue.Parameter(path!);
                return true;

            case JsonValueKind.Array:
            case JsonValueKind.Undefined:
                return Fail(RowSecurityErrorCodes.E09, "This operator takes a single value.", location, out error);

            default:
                value = new RowTemplateValue.Literal(element.Clone());
                error = null;
                return true;
        }
    }

    private static bool TryParseString(string field, JsonElement element, string location,
        out RowTemplateValue? value, out Error? error)
    {
        value = null;
        var text = element.GetString()!;

        if (string.Equals(text, PredefinedInstanceRoles.InstanceStarter, StringComparison.Ordinal))
        {
            if (!field.Equals(CreatedByField, StringComparison.OrdinalIgnoreCase))
                return Fail(RowSecurityErrorCodes.E06,
                    "$InstanceStarter may be used only on createdBy.", location, out error);
            value = new RowTemplateValue.Parameter(new RowParameterPath(RowParameterSource.CurrentUser, "ActorUserName"));
            error = null;
            return true;
        }

        if (string.Equals(text, PredefinedInstanceRoles.InstanceBehalfOfStarter, StringComparison.Ordinal))
        {
            if (!field.Equals(CreatedByBehalfOfField, StringComparison.OrdinalIgnoreCase))
                return Fail(RowSecurityErrorCodes.E06,
                    "$InstanceBehalfOfStarter may be used only on createdByBehalfOf.", location, out error);
            value = new RowTemplateValue.Parameter(new RowParameterPath(RowParameterSource.CurrentUser, "UserName"));
            error = null;
            return true;
        }

        if (RowParameterPath.LooksLikeParameter(text))
        {
            if (!RowParameterPath.TryParse(text, out var path, out _))
                return Fail(RowSecurityErrorCodes.E07, "Malformed $.context parameter.", location, out error);
            value = new RowTemplateValue.Parameter(path);
            error = null;
            return true;
        }

        value = new RowTemplateValue.Literal(element.Clone());
        error = null;
        return true;
    }

    private static bool TryParseParamObject(JsonElement element, string location,
        out RowParameterPath? path, out string? separator, out Error? error)
    {
        path = null;
        separator = null;
        string? text = null;

        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(ParamKey) && property.Value.ValueKind == JsonValueKind.String)
                text = property.Value.GetString();
            else if (property.NameEquals(SplitKey) && property.Value.ValueKind == JsonValueKind.String &&
                     !string.IsNullOrEmpty(property.Value.GetString()))
                separator = property.Value.GetString();
            else
                return Fail(RowSecurityErrorCodes.E09,
                    "A parameter object takes only a string $param and an optional non-empty string split.",
                    location, out error);
        }

        if (text is null)
            return Fail(RowSecurityErrorCodes.E09, "A parameter object requires $param.", location, out error);

        if (!RowParameterPath.TryParse(text, out path, out _))
            return Fail(RowSecurityErrorCodes.E07, "Malformed $.context parameter.", location, out error);

        error = null;
        return true;
    }

    private static bool Fail(string code, string message, string location, out Error? error)
    {
        error = Error.Validation(code, message, location);
        return false;
    }
}
