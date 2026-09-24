using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Reads the parent-defined STATE role overrides that <c>SubflowStarter</c> stamps onto a child at
/// start, from the CHILD's own <c>ExtraProperties</c>.
/// </summary>
/// <remarks>
/// <para>
/// The exact mirror of <see cref="SubFlowTransitionOverrideReader"/>, and it exists for the same reason. There
/// are two ways to reach these overrides and only one of them works at a leaf: the parent-side path
/// (<c>AuthorizeAppService</c>, which reads <c>subFlowConfig.Overrides.States</c>) needs an active
/// SubFlow correlation, and a leaf by definition has none. Evaluating a leaf through it would
/// silently find no override and fall back to the child's own <c>queryRoles</c> — quietly discarding
/// the narrowing the parent asked for.
/// </para>
/// <para>
/// <c>SubflowStarter</c> has always written this map (<c>subflow.state_role_overrides</c>) beside the
/// transition one; until now nothing in <c>src/</c> read it, so it was a dead write. It is consulted
/// from <c>TransitionAuthorizationManager.IsQueryAllowedAsync</c>, which is the single queryRoles
/// gate behind the state, data, view, schema and incident functions and behind the human-task list —
/// so a parent's narrowing now applies wherever the child is reached from, not only where a
/// particular surface remembered to look for it.
/// </para>
/// <para>
/// The stamped map is a START-TIME snapshot, the same accepted limitation the transition map has:
/// revoking an override never reaches children already in flight, and children started before the
/// stamping existed carry none at all.
/// </para>
/// </remarks>
public static class SubFlowStateOverrideReader
{
    /// <summary>
    /// Returns the query-role grants the parent stamped for <paramref name="stateKey"/>, or null when
    /// the instance carries no map, the map has no entry for that state, or it cannot be read.
    /// </summary>
    public static IReadOnlyCollection<RoleGrant>? TryReadQueryRoles(Instance instance, string stateKey)
    {
        if (string.IsNullOrEmpty(stateKey))
            return null;

        var map = SubFlowOverrideStamp.ReadStates(instance.ExtraProperties, out _);
        return map is not null
               && map.TryGetValue(stateKey, out var stateOverride)
               && stateOverride.QueryRoles is { Count: > 0 }
            ? stateOverride.QueryRoles
            : null;
    }
}
