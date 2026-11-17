using BBT.Workflow.Definitions;

namespace BBT.Workflow.Authorization.SubEndpoints;

/// <summary>
/// Turns <see cref="SubEndpointGateOptions.Grants"/> into <see cref="RoleGrant"/>s and states what the
/// gate can evaluate: with no instance, only static roles and <c>$Everyone</c> have something to
/// match. An identity leaf (<c>$InstanceStarter</c>, <c>$user.…</c>) or a <c>$role.</c> reference
/// would silently evaluate to "no match", so it is rejected instead.
/// </summary>
internal static class SubEndpointGrants
{
    /// <summary>Builds the grant set; <paramref name="errors"/> lists every reason it is unusable.</summary>
    public static IReadOnlyList<RoleGrant> Build(IReadOnlyList<SubEndpointGrantOptions> options, List<string> errors)
    {
        var grants = new List<RoleGrant>(options.Count);
        for (var index = 0; index < options.Count; index++)
        {
            var option = options[index];
            var at = $"{SubEndpointGateOptions.SectionName}:Grants:{index}";
            try
            {
                var grant = new RoleGrant(
                    option.Role?.Trim(),
                    option.Grant ?? string.Empty,
                    option.AllOf?.Select(c => new RoleGrantCondition(c.Role?.Trim() ?? string.Empty)).ToList(),
                    option.AnyOf?.Select(c => new RoleGrantCondition(c.Role?.Trim() ?? string.Empty)).ToList());

                foreach (var leaf in grant.LeafRoles)
                {
                    if (!IsEvaluableWithoutInstance(leaf))
                        errors.Add($"{at}: '{leaf}' needs an instance or an identity to match; only static roles " +
                                   $"and '{PredefinedInstanceRoles.Everyone}' can be evaluated here.");
                }

                grants.Add(grant);
            }
            catch (ArgumentException exception)
            {
                errors.Add($"{at}: {exception.Message}");
            }
        }

        return grants;
    }

    /// <summary>Whether a decision needs the caller's roles at all (any leaf other than <c>$Everyone</c>).</summary>
    public static bool NeedsCallerRoles(IReadOnlyList<RoleGrant> grants)
        => grants.Any(grant => grant.LeafRoles.Any(leaf => !PredefinedInstanceRoles.IsEveryone(leaf)));

    private static bool IsEvaluableWithoutInstance(string leaf)
        => PredefinedInstanceRoles.IsEveryone(leaf)
           || (!PredefinedInstanceRoles.IsPredefinedRole(leaf)
               && DynamicRoleGrant.Classify(leaf) == DynamicRoleFormat.NotDynamic);
}
