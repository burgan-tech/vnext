using System.Globalization;
using System.Text.Json;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Definitions.RowSecurity;
using BBT.Workflow.Definitions.Schemas;

namespace BBT.Workflow.Authorization.RowSecurity;

/// <summary>
/// Turns a flow's <c>rowSecurity</c> and one request's caller into a <see cref="RowSecurityDecision"/>
/// (spec §4, committee K1: deny by default, no <c>default</c>). Pure: no I/O, no logging, no clock.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Role resolution failed ⇒ <see cref="RowSecurityOutcome.DeniedResolutionFailed"/>, nothing else evaluated.</item>
/// <item>Static rules whose roles intersect the caller's (<c>OrdinalIgnoreCase</c>) are matched; a matched rule
/// without a filter opens every row; otherwise the base is the OR of the matched rules' predicates (a dropped
/// rule still counts as matched but contributes nothing). No match ⇒ base none.</item>
/// <item>Identity rules (<c>$InstanceStarter</c> / <c>$InstanceBehalfOfStarter</c>) add
/// <c>createdBy eq ActorUserName</c> / <c>createdByBehalfOf eq UserName</c> AND their filter, only when the
/// identity value is non-blank after trimming. Identity is read only through
/// <see cref="IRowSecurityParameterSource"/>.</item>
/// <item>Zero terms ⇒ <see cref="RowSecurityOutcome.NoRule"/> or <see cref="RowSecurityOutcome.NoContribution"/>
/// (no SQL); one term ⇒ that node; more ⇒ an <c>or</c>. Every emitted node is
/// <see cref="GraphQLFilterNode.PolicyExempt"/>; an empty node or an operator-less condition is never emitted.</item>
/// </list>
/// Parameter failures never widen (spec §4): any failure inside a rule drops the WHOLE rule. Values are trimmed,
/// blank = missing; only <c>in</c> outside an odd number of <c>not</c> narrows (unresolved elements are left out,
/// none left drops); <c>nin</c>, <c>ne</c>, <c>between</c> and every operator under <c>not</c> drop on any unresolved
/// element; a scalar fed several raw values is ambiguous; <see cref="RowSecurityOptions"/> caps, master-schema type
/// coercion and the protected-path backstop (<see cref="RowSecurityProtectedPaths"/>) each drop the rule.
/// </remarks>
public static class RowSecurityPlanner
{
    private const string CreatedByField = "createdBy";
    private const string CreatedByBehalfOfField = "createdByBehalfOf";
    private const string ActorUserNameField = "ActorUserName";
    private const string UserNameField = "UserName";

    /// <summary>Plans the row predicate for one request.</summary>
    /// <param name="input">The rule set, the caller and the request parameters.</param>
    /// <returns>The decision; never throws for request-supplied values.</returns>
    public static RowSecurityDecision Plan(RowSecurityPlanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Roles.Outcome == CallerRoleOutcome.Failed)
            return Deny(RowSecurityOutcome.DeniedResolutionFailed, [], [], []);

        var rules = input.Definition.Rules ?? [];
        var callerRoles = new HashSet<string>(
            input.Roles.Roles.Where(r => !string.IsNullOrWhiteSpace(r)), StringComparer.OrdinalIgnoreCase);

        var matched = new List<int>();
        var drops = new List<RowRuleDrop>();
        var baseTerms = new List<GraphQLFilterNode>();
        var identityTerms = new List<GraphQLFilterNode>();
        var baseAll = false;

        // Step 2: static-role matching.
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (rule.Audience != RowRuleAudience.Static || !rule.Roles!.Any(callerRoles.Contains))
                continue;

            matched.Add(i);
            if (!HasFilter(rule))
            {
                baseAll = true;
                continue;
            }

            if (TryMaterializeFilter(rule, i, input, drops) is { } node)
                baseTerms.Add(node);
        }

        if (baseAll)
            return new RowSecurityDecision(RowSecurityOutcome.Unrestricted, null, matched, MissingNames(drops), drops, null);

        // Step 3: identity add-ons.
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var (column, userField) = rule.Audience switch
            {
                RowRuleAudience.InstanceStarter => (CreatedByField, ActorUserNameField),
                RowRuleAudience.InstanceBehalfOfStarter => (CreatedByBehalfOfField, UserNameField),
                _ => (null, null)
            };
            if (column is null)
                continue;

            var identity = ReadIdentity(input.Parameters, userField!);
            if (identity is null)
                continue;

            matched.Add(i);
            var identityNode = Leaf(column, new FieldCondition { Eq = identity });
            if (!HasFilter(rule))
            {
                identityTerms.Add(identityNode);
                continue;
            }

            if (TryMaterializeFilter(rule, i, input, drops) is { } filterNode)
                identityTerms.Add(new GraphQLFilterNode { And = [identityNode, filterNode], PolicyExempt = true });
        }

        matched.Sort();

        // Step 4: combine.
        var terms = baseTerms.Concat(identityTerms).ToList();
        if (terms.Count == 0)
        {
            return Deny(matched.Count == 0 ? RowSecurityOutcome.NoRule : RowSecurityOutcome.NoContribution,
                matched, drops, MissingNames(drops));
        }

        var predicate = terms.Count == 1 ? terms[0] : new GraphQLFilterNode { Or = terms, PolicyExempt = true };
        return new RowSecurityDecision(RowSecurityOutcome.Filtered, predicate, matched, MissingNames(drops), drops, null);
    }

    private static RowSecurityDecision Deny(RowSecurityOutcome outcome, IReadOnlyList<int> matched,
        IReadOnlyList<RowRuleDrop> drops, IReadOnlyList<string> missing) =>
        new(outcome, null, matched, missing, drops, null);

    private static IReadOnlyList<string> MissingNames(List<RowRuleDrop> drops) =>
        drops.Where(d => d.Reason == RowRuleDropReasons.Missing)
            .SelectMany(d => d.ParameterNames)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    // "filter": null is treated as absent (Task 1 ruling).
    private static bool HasFilter(RowSecurityRule rule) =>
        rule.Filter is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };

    private static string? ReadIdentity(IRowSecurityParameterSource parameters, string field)
    {
        var values = parameters.Get(new RowParameterPath(RowParameterSource.CurrentUser, field));
        if (values.Count != 1)
            return null;

        var value = values[0]?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Materializes a rule's filter; on any failure records a drop and returns null (the whole rule
    /// contributes nothing, never a sub-tree).
    /// </summary>
    private static GraphQLFilterNode? TryMaterializeFilter(RowSecurityRule rule, int index, RowSecurityPlanInput input,
        List<RowRuleDrop> drops)
    {
        var parsed = RowFilterTemplateParser.Parse(rule.Filter!.Value);
        if (!parsed.IsSuccess)
        {
            drops.Add(new RowRuleDrop(index, RowRuleDropReasons.Invalid, []));
            return null;
        }

        var root = parsed.Value!.Root;

        // Runtime backstop (R-F6(c), R-F14): publish may have skipped the schema-aware check (RS-W0), so a rule
        // that touches a refused protected path is dropped here whatever its parameters resolve to.
        var protectedFailure = new MaterializeFailure();
        if (TouchesRefusedPath(root, input.Exposure, protectedFailure))
        {
            drops.Add(new RowRuleDrop(index, RowRuleDropReasons.ProtectedPath, protectedFailure.ParameterNames));
            return null;
        }

        var failure = new MaterializeFailure();
        var node = TryMaterialize(root, input, failure, negated: false);
        if (node is not null)
            return node;

        drops.Add(new RowRuleDrop(index, failure.Reason ?? RowRuleDropReasons.Invalid, failure.ParameterNames));
        return null;
    }

    private static bool TouchesRefusedPath(RowTemplateNode node, SchemaExposureMetadata exposure, MaterializeFailure failure)
    {
        switch (node)
        {
            case RowTemplateAnd and:
                return and.Children.Any(c => TouchesRefusedPath(c, exposure, failure));
            case RowTemplateOr or:
                return or.Children.Any(c => TouchesRefusedPath(c, exposure, failure));
            case RowTemplateNot not:
                return TouchesRefusedPath(not.Child, exposure, failure);
            case RowTemplateLeaf leaf when !leaf.IsInstanceColumn:
            {
                // One verdict per value source (null = literal); the strongest wins, as at publish.
                var paths = ParameterPaths(leaf.Value).ToList();
                var sources = paths.Select(p => (RowParameterSource?)p.Source).ToList();
                if (sources.Count == 0 || HasLiteral(leaf.Value))
                    sources.Add(null);
                var protection = sources.Max(s => RowSecurityProtectedPaths.Classify(leaf.Field, exposure, s));
                if (!RowSecurityProtectedPaths.IsRefused(protection))
                    return false;

                foreach (var path in paths)
                    failure.Fail<object>(RowRuleDropReasons.ProtectedPath, path);
                return true;
            }
            default:
                return false;
        }
    }

    private static bool HasLiteral(RowTemplateValue value) => value switch
    {
        RowTemplateValue.Literal => true,
        RowTemplateValue.Elements e => e.Items.Any(HasLiteral),
        _ => false
    };

    private static IEnumerable<RowParameterPath> ParameterPaths(RowTemplateValue value) => value switch
    {
        RowTemplateValue.Parameter p => [p.Path],
        RowTemplateValue.SplitParameter s => [s.Path],
        RowTemplateValue.Elements e => e.Items.SelectMany(ParameterPaths),
        _ => []
    };

    private sealed class MaterializeFailure
    {
        public string? Reason { get; set; }

        public List<string> ParameterNames { get; } = [];

        /// <summary>Records the first failure reason and the parameter name; always returns null.</summary>
        public T? Fail<T>(string reason, RowParameterPath? path = null) where T : class
        {
            Reason ??= reason;
            if (path is not null && !ParameterNames.Contains(path.Name, StringComparer.Ordinal))
                ParameterNames.Add(path.Name);
            return null;
        }
    }

    /// <summary>
    /// Turns a template node into a policy-exempt filter node, or null when any leaf fails.
    /// </summary>
    /// <param name="node">The template node.</param>
    /// <param name="input">The planner input.</param>
    /// <param name="failure">Collects the first failure reason and the parameter names.</param>
    /// <param name="negated">True under an odd number of <c>not</c>: no leaf may narrow, every failure drops.</param>
    private static GraphQLFilterNode? TryMaterialize(RowTemplateNode node, RowSecurityPlanInput input,
        MaterializeFailure failure, bool negated)
    {
        switch (node)
        {
            case RowTemplateAnd and:
                var andChildren = MaterializeAll(and.Children, input, failure, negated);
                return andChildren is null ? null : new GraphQLFilterNode { And = andChildren, PolicyExempt = true };

            case RowTemplateOr or:
                var orChildren = MaterializeAll(or.Children, input, failure, negated);
                return orChildren is null ? null : new GraphQLFilterNode { Or = orChildren, PolicyExempt = true };

            case RowTemplateNot not:
                var child = TryMaterialize(not.Child, input, failure, !negated);
                return child is null ? null : new GraphQLFilterNode { Not = child, PolicyExempt = true };

            case RowTemplateLeaf leaf:
                var condition = TryMaterializeLeaf(leaf, input, failure, negated);
                return condition is null ? null : Leaf(leaf.Field, condition);

            default:
                return failure.Fail<GraphQLFilterNode>(RowRuleDropReasons.Invalid);
        }
    }

    private static List<GraphQLFilterNode>? MaterializeAll(IReadOnlyList<RowTemplateNode> children,
        RowSecurityPlanInput input, MaterializeFailure failure, bool negated)
    {
        var list = new List<GraphQLFilterNode>(children.Count);
        foreach (var child in children)
        {
            var node = TryMaterialize(child, input, failure, negated);
            if (node is null)
                return null;
            list.Add(node);
        }

        return list.Count == 0 ? null : list;
    }

    private static GraphQLFilterNode Leaf(string field, FieldCondition condition) => new()
    {
        Attributes = new Dictionary<string, FieldCondition>(StringComparer.Ordinal) { [field] = condition },
        PolicyExempt = true
    };

    /// <summary>Per-leaf resolution settings.</summary>
    /// <param name="Narrowing">True only for <c>in</c> outside an odd <c>not</c>: unresolved elements are dropped.</param>
    /// <param name="FieldType">The master-schema type parameter values are coerced to; null = keep strings.</param>
    /// <param name="Limits">Caps.</param>
    private sealed record LeafContext(bool Narrowing, string? FieldType, RowSecurityOptions Limits);

    private static FieldCondition? TryMaterializeLeaf(RowTemplateLeaf leaf, RowSecurityPlanInput input,
        MaterializeFailure failure, bool negated)
    {
        // Text operators match on text whatever the field type; everything else is coerced to the field type.
        var coerced = leaf.Operator is not ("like" or "match" or "startswith" or "endswith");
        var fieldType = coerced && !leaf.IsInstanceColumn ? input.Schema?.GetFieldMetadata(leaf.Field)?.Type : null;
        var context = new LeafContext(leaf.Operator == "in" && !negated, fieldType, input.Limits);

        switch (leaf.Operator)
        {
            case "in" or "nin" or "between":
            {
                var values = ResolveArray(leaf.Value, leaf.Operator, input.Parameters, context, failure);
                if (values is null)
                    return null;
                if (leaf.Operator == "between" && values.Length != 2)
                    return failure.Fail<FieldCondition>(RowRuleDropReasons.Arity, ParameterPaths(leaf.Value).FirstOrDefault());

                return leaf.Operator switch
                {
                    "in" => new FieldCondition { In = values },
                    "nin" => new FieldCondition { NotIn = values },
                    _ => new FieldCondition { Between = values }
                };
            }

            case "isNull":
                return leaf.Value is RowTemplateValue.Literal { Value.ValueKind: JsonValueKind.True or JsonValueKind.False } flag
                    ? new FieldCondition { IsNull = flag.Value.GetBoolean() }
                    : failure.Fail<FieldCondition>(RowRuleDropReasons.Invalid);

            case "includes":
                return leaf.Value is RowTemplateValue.Literal { Value.ValueKind: JsonValueKind.Object } doc
                    ? new FieldCondition { Includes = doc.Value.Clone() }
                    : failure.Fail<FieldCondition>(RowRuleDropReasons.Invalid);

            default:
            {
                var value = ResolveScalar(leaf.Value, input.Parameters, context, failure);
                if (value is null)
                    return null;

                return leaf.Operator switch
                {
                    "eq" => new FieldCondition { Eq = value },
                    "ne" => new FieldCondition { Ne = value },
                    "gt" => new FieldCondition { Gt = value },
                    "ge" => new FieldCondition { Ge = value },
                    "lt" => new FieldCondition { Lt = value },
                    "le" => new FieldCondition { Le = value },
                    "like" => new FieldCondition { Like = Text(value) },
                    "match" => new FieldCondition { Match = Text(value) },
                    "startswith" => new FieldCondition { StartsWith = Text(value) },
                    "endswith" => new FieldCondition { EndsWith = Text(value) },
                    _ => failure.Fail<FieldCondition>(RowRuleDropReasons.Invalid)
                };
            }
        }
    }

    private static string Text(object value) =>
        Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    private static object? ResolveScalar(RowTemplateValue value, IRowSecurityParameterSource parameters,
        LeafContext context, MaterializeFailure failure)
    {
        switch (value)
        {
            case RowTemplateValue.Literal literal:
                return LiteralValue(literal.Value) ?? failure.Fail<object>(RowRuleDropReasons.Invalid);

            case RowTemplateValue.Parameter parameter:
            {
                var values = parameters.Get(parameter.Path);
                // Never pick a "first value": a scalar fed several raw values contributes nothing.
                if (values.Count > 1)
                    return failure.Fail<object>(RowRuleDropReasons.Ambiguous, parameter.Path);
                var single = values.Count == 1 ? values[0]?.Trim() : null;
                return string.IsNullOrEmpty(single)
                    ? failure.Fail<object>(RowRuleDropReasons.Missing, parameter.Path)
                    : Accept(single, parameter.Path, context, failure);
            }

            default:
                return failure.Fail<object>(RowRuleDropReasons.Invalid);
        }
    }

    /// <summary>Applies the length cap and the type coercion to one trimmed, non-blank parameter value.</summary>
    private static object? Accept(string value, RowParameterPath path, LeafContext context, MaterializeFailure failure)
    {
        if (value.Length > context.Limits.MaxElementLength)
            return failure.Fail<object>(RowRuleDropReasons.Cap, path);
        return TryCoerce(value, context.FieldType, out var coerced)
            ? coerced
            : failure.Fail<object>(RowRuleDropReasons.Coercion, path);
    }

    private static object[]? ResolveArray(RowTemplateValue value, string op, IRowSecurityParameterSource parameters,
        LeafContext context, MaterializeFailure failure)
    {
        var items = new List<object>();
        switch (value)
        {
            case RowTemplateValue.Elements elements:
                foreach (var item in elements.Items)
                {
                    if (item is RowTemplateValue.Parameter parameter)
                    {
                        var raw = parameters.Get(parameter.Path);
                        if (raw.Count > 1)
                        {
                            // An element is one value; between counts its bounds, the others refuse to guess.
                            return failure.Fail<object[]>(
                                op == "between" ? RowRuleDropReasons.Arity : RowRuleDropReasons.Ambiguous, parameter.Path);
                        }

                        var single = raw.Count == 1 ? raw[0]?.Trim() : null;
                        if (string.IsNullOrEmpty(single))
                        {
                            if (context.Narrowing)
                                continue; // narrowing in: an unresolved element only removes itself
                            return failure.Fail<object[]>(RowRuleDropReasons.Missing, parameter.Path);
                        }

                        if (Accept(single, parameter.Path, context, failure) is not { } accepted)
                            return null;
                        items.Add(accepted);
                    }
                    else if (ResolveScalar(item, parameters, context, failure) is { } literal)
                    {
                        items.Add(literal);
                    }
                    else
                    {
                        return null;
                    }
                }

                if (items.Count == 0)
                {
                    var first = ParameterPaths(elements).FirstOrDefault();
                    return failure.Fail<object[]>(RowRuleDropReasons.Missing, first);
                }

                break;

            case RowTemplateValue.SplitParameter split:
            {
                var raw = parameters.Get(split.Path);
                var segments = (split.Separator is null
                    ? raw.Select(v => v?.Trim() ?? string.Empty)
                    : raw.SelectMany(v => (v ?? string.Empty).Split(split.Separator)).Select(v => v.Trim())).ToList();

                // A blank segment is an unresolved element: narrowing in drops it, nin / anything under not
                // drops the whole rule (dropping it silently would widen).
                if (!context.Narrowing && segments.Any(string.IsNullOrEmpty))
                    return failure.Fail<object[]>(RowRuleDropReasons.Missing, split.Path);
                segments.RemoveAll(string.IsNullOrEmpty);
                if (segments.Count == 0)
                    return failure.Fail<object[]>(RowRuleDropReasons.Missing, split.Path);
                if (segments.Count > context.Limits.MaxElements)
                    return failure.Fail<object[]>(RowRuleDropReasons.Cap, split.Path);

                foreach (var segment in segments)
                {
                    if (Accept(segment, split.Path, context, failure) is not { } accepted)
                        return null;
                    items.Add(accepted);
                }

                break;
            }

            default:
                return failure.Fail<object[]>(RowRuleDropReasons.Invalid);
        }

        if (items.Count > context.Limits.MaxElements)
            return failure.Fail<object[]>(RowRuleDropReasons.Cap, ParameterPaths(value).FirstOrDefault());

        return items.ToArray();
    }

    /// <summary>
    /// Coerces one request-supplied value to the master-schema field type: <c>number</c> / <c>integer</c> ⇒
    /// <see cref="decimal"/> (invariant culture, no thousands separator; <c>integer</c> must be integral),
    /// <c>boolean</c> ⇒ <see cref="bool"/>, anything else (incl. no schema / unknown field) ⇒ the string as is.
    /// </summary>
    /// <param name="raw">The trimmed value.</param>
    /// <param name="type">The JSON Schema type, or null.</param>
    /// <param name="value">The coerced value.</param>
    /// <returns>False when the value does not fit the type (the rule is dropped).</returns>
    internal static bool TryCoerce(string raw, string? type, out object value)
    {
        value = raw;
        switch (type)
        {
            case "number" or "integer":
                if (!decimal.TryParse(raw, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint |
                        NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var number) ||
                    (type == "integer" && number != decimal.Truncate(number)))
                    return false;
                value = number;
                return true;

            case "boolean":
                if (!bool.TryParse(raw, out var flag))
                    return false;
                value = flag;
                return true;

            default:
                return true;
        }
    }

    // Mirrors GraphQLFilterNodeConverter.ReadValue so a planned literal compiles like a wire literal.
    private static object? LiteralValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var l) => l,
        JsonValueKind.Number when element.TryGetDecimal(out var d) => d,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}
