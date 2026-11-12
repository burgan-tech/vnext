namespace BBT.Workflow.Definitions;

/// <summary>
/// Publish-time rules for <see cref="RoleGrant"/> lists. Static role names and the five predefined
/// roles are free-form, except that a case variant of <c>$Everyone</c> is refused (it would silently be a
/// static role) and <c>$Everyone</c> itself is refused in an exemption list; only leaves that declare dynamic-role intent via a qualifier prefix are
/// checked, against the exact rules the runtime parser applies (<see cref="DynamicRoleGrant.Classify"/>
/// cannot drift from <see cref="DynamicRoleGrant.TryParse"/>). A combinator is checked leaf by leaf.
/// </summary>
public static class RoleGrantDefinitionRules
{
    private const string ContextPrefix = "$.context.";

    /// <summary>Validates every leaf role of <paramref name="grant"/>; yields one message per problem.</summary>
    public static IEnumerable<string> Validate(RoleGrant grant, string context)
    {
        foreach (var leaf in grant.LeafRoles)
        {
            if (string.Equals(leaf.Trim(), PredefinedInstanceRoles.Everyone, StringComparison.OrdinalIgnoreCase) &&
                !PredefinedInstanceRoles.IsEveryone(leaf))
            {
                yield return $"Role '{leaf}' in '{context}' looks like {PredefinedInstanceRoles.Everyone}; predefined roles are case-sensitive, write '{PredefinedInstanceRoles.Everyone}'.";
                continue;
            }

            var message = DynamicRoleGrant.Classify(leaf) switch
            {
                DynamicRoleFormat.MissingContextPrefix =>
                    $"Dynamic role '{leaf}' in '{context}' has an invalid path. Path must start with '{ContextPrefix}' (case-sensitive).",
                DynamicRoleFormat.EmptyNavigationPath =>
                    $"Dynamic role '{leaf}' in '{context}' has an empty navigation path after '{ContextPrefix}'.",
                _ => null
            };

            if (message != null)
                yield return message;
        }
    }

    /// <summary>x-masking / x-encryption exemption lists: plain allow grants only.</summary>
    public static IEnumerable<string> ValidateExemption(RoleGrant grant, string context)
    {
        if (grant.IsCombinator)
            yield return $"'{context}' is an exemption list: 'allOf' / 'anyOf' are not accepted, use {{ \"role\", \"grant\": \"allow\" }}.";

        if (grant.LeafRoles.Any(PredefinedInstanceRoles.IsEveryone))
            yield return $"'{context}' is an exemption list: {PredefinedInstanceRoles.Everyone} would exempt every caller and is not accepted.";

        foreach (var message in Validate(grant, context))
            yield return message;
    }
}
