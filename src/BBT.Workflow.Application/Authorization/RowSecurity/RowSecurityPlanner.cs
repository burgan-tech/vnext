using System.Text.Json;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Definitions.RowSecurity;

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
    /// contributes nothing).
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

        var failure = new MaterializeFailure();
        var node = TryMaterialize(parsed.Value!.Root, input, failure);
        if (node is not null)
            return node;

        drops.Add(new RowRuleDrop(index, failure.Reason ?? RowRuleDropReasons.Invalid, failure.ParameterNames));
        return null;
    }

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
    /// <remarks>
    /// Task 5A semantics are the fail-closed baseline: any unresolved parameter drops the whole rule.
    /// Task 5B refines leaf resolution (narrowing <c>in</c>, polarity, caps, coercion, protected paths).
    /// </remarks>
    private static GraphQLFilterNode? TryMaterialize(RowTemplateNode node, RowSecurityPlanInput input, MaterializeFailure failure)
    {
        switch (node)
        {
            case RowTemplateAnd and:
                var andChildren = MaterializeAll(and.Children, input, failure);
                return andChildren is null ? null : new GraphQLFilterNode { And = andChildren, PolicyExempt = true };

            case RowTemplateOr or:
                var orChildren = MaterializeAll(or.Children, input, failure);
                return orChildren is null ? null : new GraphQLFilterNode { Or = orChildren, PolicyExempt = true };

            case RowTemplateNot not:
                var child = TryMaterialize(not.Child, input, failure);
                return child is null ? null : new GraphQLFilterNode { Not = child, PolicyExempt = true };

            case RowTemplateLeaf leaf:
                var condition = TryMaterializeLeaf(leaf, input, failure);
                return condition is null ? null : Leaf(leaf.Field, condition);

            default:
                return failure.Fail<GraphQLFilterNode>(RowRuleDropReasons.Invalid);
        }
    }

    private static List<GraphQLFilterNode>? MaterializeAll(IReadOnlyList<RowTemplateNode> children,
        RowSecurityPlanInput input, MaterializeFailure failure)
    {
        var list = new List<GraphQLFilterNode>(children.Count);
        foreach (var child in children)
        {
            var node = TryMaterialize(child, input, failure);
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

    private static FieldCondition? TryMaterializeLeaf(RowTemplateLeaf leaf, RowSecurityPlanInput input, MaterializeFailure failure)
    {
        switch (leaf.Operator)
        {
            case "in" or "nin" or "between":
            {
                var values = ResolveArray(leaf.Value, input.Parameters, failure);
                if (values is null)
                    return null;
                if (values.Length == 0)
                    return failure.Fail<FieldCondition>(RowRuleDropReasons.Missing);
                if (leaf.Operator == "between" && values.Length != 2)
                    return failure.Fail<FieldCondition>(RowRuleDropReasons.Arity);

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
                var value = ResolveScalar(leaf.Value, input.Parameters, failure);
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
        Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private static object? ResolveScalar(RowTemplateValue value, IRowSecurityParameterSource parameters, MaterializeFailure failure)
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
                    : single;
            }

            default:
                return failure.Fail<object>(RowRuleDropReasons.Invalid);
        }
    }

    private static object[]? ResolveArray(RowTemplateValue value, IRowSecurityParameterSource parameters, MaterializeFailure failure)
    {
        switch (value)
        {
            case RowTemplateValue.Elements elements:
            {
                var items = new List<object>(elements.Items.Count);
                foreach (var item in elements.Items)
                {
                    var resolved = ResolveScalar(item, parameters, failure);
                    if (resolved is null)
                        return null;
                    items.Add(resolved);
                }

                return items.ToArray();
            }

            case RowTemplateValue.SplitParameter split:
            {
                var raw = parameters.Get(split.Path);
                var items = (split.Separator is null
                    ? raw.Select(v => v?.Trim())
                    : raw.SelectMany(v => (v ?? string.Empty).Split(split.Separator)).Select(v => v.Trim())).ToList();
                // 5A fail-closed baseline: a blank segment is an unresolved element and drops the rule for
                // every operator (dropping it silently would widen nin / anything under not). Task 5B adds
                // the in-only narrowing.
                if (items.Count == 0 || items.Any(string.IsNullOrEmpty))
                    return failure.Fail<object[]>(RowRuleDropReasons.Missing, split.Path);
                return items.Cast<object>().ToArray();
            }

            default:
                return failure.Fail<object[]>(RowRuleDropReasons.Invalid);
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
