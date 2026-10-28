using System.Text.Json;

namespace BBT.Workflow.Definitions.RowSecurity;

/// <summary>Severity of a <see cref="RowSecurityFinding"/>.</summary>
public enum RowSecurityFindingSeverity
{
    /// <summary>Blocks publishing.</summary>
    Error,

    /// <summary>Logged by the publish path; never blocks.</summary>
    Warning
}

/// <summary>
/// One publish-time finding about an <c>attributes.rowSecurity</c> declaration.
/// </summary>
/// <param name="Severity">Error or warning.</param>
/// <param name="Code">The <c>RS-*</c> code.</param>
/// <param name="Path">The member path, e.g. <c>rowSecurity.rules[0].roles[1]</c>.</param>
/// <param name="Message">English message; never echoes authored values.</param>
public sealed record RowSecurityFinding(RowSecurityFindingSeverity Severity, string Code, string Path, string Message);

/// <summary>
/// Schema-blind publish rules for <see cref="RowSecurityDefinition"/> (vnext#1107): role shape,
/// filter shape and parameter placement. Checks that need the master schema or the flow key
/// (RS-E08, E14..E16, E18) live elsewhere.
/// </summary>
public static class RowSecurityDefinitionRules
{
    private const string Root = "rowSecurity";
    private const string WarningW4 = "RS-W4";
    private const string WarningW6Query = "RS-W6q";
    private const string WarningW2b = "RS-W2b";

    private static readonly string[] TextOperators = ["like", "match", "startswith", "endswith"];

    /// <summary>
    /// Validates <paramref name="definition"/>.
    /// </summary>
    /// <param name="definition">The declared row security.</param>
    /// <returns>Every finding; the declaration is publishable when none has <see cref="RowSecurityFindingSeverity.Error"/>.</returns>
    public static IReadOnlyList<RowSecurityFinding> Validate(RowSecurityDefinition definition)
    {
        var findings = new List<RowSecurityFinding>();

        if (definition.UnknownMembers is { Count: > 0 })
        {
            foreach (var name in definition.UnknownMembers.Keys)
                findings.Add(Error(RowSecurityErrorCodes.E12, $"{Root}.{name}",
                    "Unknown member on rowSecurity; only rules is supported."));
        }

        if (definition.Rules is null || definition.Rules.Count == 0)
        {
            findings.Add(Error(RowSecurityErrorCodes.E12, $"{Root}.rules",
                "rules is required and must contain at least one rule."));
            return findings;
        }

        for (var i = 0; i < definition.Rules.Count; i++)
            ValidateRule(definition.Rules[i], $"{Root}.rules[{i}]", findings);

        return findings;
    }

    private static void ValidateRule(RowSecurityRule? rule, string path, List<RowSecurityFinding> findings)
    {
        if (rule is null)
        {
            findings.Add(Error(RowSecurityErrorCodes.E12, path, "A rule must be an object."));
            return;
        }

        if (rule.UnknownMembers is { Count: > 0 })
        {
            foreach (var name in rule.UnknownMembers.Keys)
                findings.Add(Error(RowSecurityErrorCodes.E12, $"{path}.{name}",
                    "Unknown member on a rule; only roles and filter are supported."));
        }

        var flaggedIdentity = ValidateRoles(rule.Roles, $"{path}.roles", findings);
        if (flaggedIdentity)
            findings.Add(Warning(WarningW2b, path,
                "Identity rule filters on createdBy / createdByBehalfOf, which have no index; large flows may scan."));

        if (rule.Filter is not { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } filter)
            return;

        var parsed = RowFilterTemplateParser.Parse(filter);
        if (!parsed.IsSuccess)
        {
            var error = parsed.Error;
            findings.Add(Error(error.Code, string.IsNullOrEmpty(error.Target) ? $"{path}.filter" : $"{path}.{error.Target}",
                error.Message));
            return;
        }

        ValidateNode(parsed.Value!.Root, $"{path}.filter", findings, ref flaggedIdentity, path);
    }

    /// <summary>Validates the roles; returns true when the rule is an identity rule ($InstanceStarter / $InstanceBehalfOfStarter).</summary>
    private static bool ValidateRoles(List<string>? roles, string path, List<RowSecurityFinding> findings)
    {
        if (roles is null || roles.Count == 0)
        {
            findings.Add(Error(RowSecurityErrorCodes.E12, path, "roles is required and must not be empty."));
            return false;
        }

        var hasStatic = false;
        var hasStarter = false;
        var hasBehalfOfStarter = false;

        for (var j = 0; j < roles.Count; j++)
        {
            var role = roles[j];
            var rolePath = $"{path}[{j}]";

            if (string.IsNullOrWhiteSpace(role))
            {
                findings.Add(Error(RowSecurityErrorCodes.E12, rolePath, "A role must not be blank."));
            }
            else if (role is PredefinedInstanceRoles.PreviousUser or PredefinedInstanceRoles.PreviousBehalfOfUser)
            {
                findings.Add(Error(RowSecurityErrorCodes.E01, rolePath,
                    "$PreviousUser and $PreviousBehalfOfUser cannot be used in a row-security rule: no instance is in scope when a list is filtered."));
            }
            else if (DynamicRoleGrant.Classify(role) != DynamicRoleFormat.NotDynamic)
            {
                findings.Add(Error(RowSecurityErrorCodes.E02, rolePath,
                    "Dynamic role grants ($user. / $userBehalfOf. / $role.) cannot be used in a row-security rule: they need an instance."));
            }
            else if (string.Equals(role, PredefinedInstanceRoles.InstanceStarter, StringComparison.Ordinal))
            {
                hasStarter = true;
            }
            else if (string.Equals(role, PredefinedInstanceRoles.InstanceBehalfOfStarter, StringComparison.Ordinal))
            {
                hasBehalfOfStarter = true;
            }
            else if (role.StartsWith('$'))
            {
                findings.Add(Error(RowSecurityErrorCodes.E05, rolePath,
                    "Only $InstanceStarter and $InstanceBehalfOfStarter may be used as reserved roles in a row-security rule."));
            }
            else
            {
                hasStatic = true;
            }
        }

        var kinds = (hasStatic ? 1 : 0) + (hasStarter ? 1 : 0) + (hasBehalfOfStarter ? 1 : 0);
        if (kinds > 1)
        {
            findings.Add(Error(RowSecurityErrorCodes.E04, path,
                "A rule must hold either static roles or exactly one reserved role, not a mix."));
            return false;
        }

        return hasStarter || hasBehalfOfStarter;
    }

    private static void ValidateNode(RowTemplateNode node, string path, List<RowSecurityFinding> findings,
        ref bool flaggedIdentity, string rulePath)
    {
        switch (node)
        {
            case RowTemplateAnd and:
                foreach (var child in and.Children)
                    ValidateNode(child, path, findings, ref flaggedIdentity, rulePath);
                break;
            case RowTemplateOr or:
                foreach (var child in or.Children)
                    ValidateNode(child, path, findings, ref flaggedIdentity, rulePath);
                break;
            case RowTemplateNot not:
                ValidateNode(not.Child, path, findings, ref flaggedIdentity, rulePath);
                break;
            case RowTemplateLeaf leaf:
                ValidateLeaf(leaf, $"{path}.{leaf.Field}.{leaf.Operator}", findings, ref flaggedIdentity, rulePath);
                break;
        }
    }

    private static void ValidateLeaf(RowTemplateLeaf leaf, string path, List<RowSecurityFinding> findings,
        ref bool flaggedIdentity, string rulePath)
    {
        if (!flaggedIdentity &&
            (leaf.Field.Equals("createdBy", StringComparison.OrdinalIgnoreCase) ||
             leaf.Field.Equals("createdByBehalfOf", StringComparison.OrdinalIgnoreCase)))
        {
            flaggedIdentity = true;
            findings.Add(Warning(WarningW2b, rulePath,
                "A filter on createdBy / createdByBehalfOf has no index; large flows may scan."));
        }

        var textOperator = TextOperators.Contains(leaf.Operator);

        foreach (var value in Flatten(leaf.Value))
        {
            switch (value)
            {
                case RowTemplateValue.Literal literal:
                    ValidateLiteral(leaf.Operator, literal.Value, path, findings);
                    break;
                case RowTemplateValue.Parameter parameter:
                    ValidateParameter(parameter.Path, textOperator, path, findings);
                    break;
                case RowTemplateValue.SplitParameter split:
                    ValidateParameter(split.Path, textOperator, path, findings);
                    break;
            }
        }
    }

    private static void ValidateLiteral(string op, JsonElement value, string path, List<RowSecurityFinding> findings)
    {
        if (op == "includes")
        {
            if (ContainsParameterText(value))
                findings.Add(Error(RowSecurityErrorCodes.E17, path,
                    "Parameters are not allowed inside includes."));
            return;
        }

        if (op == "isNull")
            return;

        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False)
        {
            findings.Add(Error(RowSecurityErrorCodes.E09, path,
                "A null or boolean literal operand is not allowed; only isNull takes a boolean."));
            return;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            if (text.StartsWith(RowParameterPath.Prefix, StringComparison.OrdinalIgnoreCase) &&
                !RowParameterPath.LooksLikeParameter(text))
            {
                findings.Add(Warning(WarningW4, path,
                    "A value that starts with $.context. in a different letter case is treated as a literal, not a parameter."));
            }
        }
    }

    private static void ValidateParameter(RowParameterPath parameter, bool textOperator, string path,
        List<RowSecurityFinding> findings)
    {
        if (textOperator)
            findings.Add(Error(RowSecurityErrorCodes.E17, path,
                "Parameters are not allowed on like / match / startswith / endswith."));

        if (parameter.Source == RowParameterSource.QueryParameters)
            findings.Add(Warning(WarningW6Query, path,
                "QueryParameters are client-controlled and are not a security boundary."));
    }

    private static IEnumerable<RowTemplateValue> Flatten(RowTemplateValue value)
    {
        if (value is RowTemplateValue.Elements elements)
        {
            foreach (var item in elements.Items)
                yield return item;
        }
        else
        {
            yield return value;
        }
    }

    private static bool ContainsParameterText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()!.StartsWith(RowParameterPath.Prefix, StringComparison.Ordinal),
        JsonValueKind.Object => element.EnumerateObject().Any(p => ContainsParameterText(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(ContainsParameterText),
        _ => false
    };

    private static RowSecurityFinding Error(string code, string path, string message) =>
        new(RowSecurityFindingSeverity.Error, code, path, message);

    private static RowSecurityFinding Warning(string code, string path, string message) =>
        new(RowSecurityFindingSeverity.Warning, code, path, message);
}
