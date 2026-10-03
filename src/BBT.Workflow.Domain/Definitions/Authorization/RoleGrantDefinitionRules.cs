namespace BBT.Workflow.Definitions;

/// <summary>
/// Publish-time rules for <see cref="RoleGrant"/> lists. Static role names and the four predefined
/// instance roles are free-form; only leaves that declare dynamic-role intent via a qualifier prefix are
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

    /// <summary>x-masking / x-encryption exemption lists: plain allow grants only (decision K1).</summary>
    public static IEnumerable<string> ValidateExemption(RoleGrant grant, string context)
    {
        if (grant.IsCombinator)
            yield return $"'{context}' is an exemption list: 'allOf' / 'anyOf' are not accepted, use {{ \"role\", \"grant\": \"allow\" }}.";

        foreach (var message in Validate(grant, context))
            yield return message;
    }
}
