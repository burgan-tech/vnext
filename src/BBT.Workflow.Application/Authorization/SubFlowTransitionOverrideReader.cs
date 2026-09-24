using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Reads the parent-defined transition role overrides that <c>SubflowStarter</c> stamps onto a
/// child at start, from the CHILD's own <c>ExtraProperties</c>.
/// </summary>
/// <remarks>
/// <para>
/// There are two ways to reach these overrides and only one of them works at a leaf. The
/// parent-side reader takes a parent instance and its workflow and looks up
/// <c>state.SubFlow.Overrides.Transitions</c> — it returns null whenever the instance has no active
/// SubFlow correlation, which is true of every leaf by definition. Evaluating a leaf through it
/// would silently find no overrides, and a child transition authored with no <c>roles</c> of its
/// own, gated solely by its parent's override, would become visible to every caller: an empty grant
/// set allows.
/// </para>
/// <para>
/// The stamped map is a START-TIME snapshot, which is a real and accepted limitation: revoking an
/// override never reaches children already in flight, and children started before the stamping
/// existed carry no map at all. That is the same semantics the state function has always had for
/// subflow transitions, so reading it here converges the two surfaces rather than inventing a third.
/// </para>
/// </remarks>
public static class SubFlowTransitionOverrideReader
{
    /// <summary>
    /// The grants the parent stamped for one transition, or null when it stamped none for it.
    /// </summary>
    /// <remarks>
    /// The per-key form exists so every authorization surface resolves the override the SAME way.
    /// It is consulted from <see cref="TransitionAuthorizationManager"/>, which the state function,
    /// <c>authorize</c> and the authorization matrix all go through — resolving it per surface
    /// instead is how they came to disagree: measured at a leaf whose parent had narrowed it, the
    /// state function honoured the narrowing while <c>authorize</c>, which resolved overrides from
    /// the PARENT's definition and finds none at a leaf, answered from the child's own grants and
    /// said the opposite for both roles.
    /// </remarks>
    public static IReadOnlyCollection<RoleGrant>? TryReadRoles(Instance instance, string transitionKey)
    {
        if (string.IsNullOrEmpty(transitionKey))
            return null;

        var map = TryRead(instance);

        return map is not null
               && map.TryGetValue(transitionKey, out var tOverride)
               && tOverride.Roles is { Count: > 0 }
            ? tOverride.Roles
            : null;
    }

    /// <summary>
    /// Returns the stamped override map, or null when the instance carries none or it cannot be read.
    /// </summary>
    public static Dictionary<string, SubFlowTransitionOverride>? TryRead(Instance instance)
        => SubFlowOverrideStamp.ReadTransitions(instance.ExtraProperties, out _);
}
