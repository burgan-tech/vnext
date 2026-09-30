using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// Long-poll configuration in force for one instance in one state: the state's own declaration with
/// the parent's field-level override applied.
/// </summary>
public sealed record EffectiveLongPoll(
    bool Terminate,
    int FallbackTimeoutSeconds,
    IReadOnlyCollection<RoleGrant> Roles,
    ScriptCode? Rule);

/// <summary>
/// Result of <see cref="InstanceSubFlowOverrideExtensions.ResolveEffectiveLongPoll"/>: the effective
/// long-poll (null when the state declares none) plus why a present override was not fully applied.
/// </summary>
public readonly record struct EffectiveLongPollResolution(
    EffectiveLongPoll? LongPoll,
    bool OverridePresent,
    bool OverrideIgnoredNoLongPoll,
    bool RolesOverrideIgnoredRuleArm,
    bool StampMalformed);

/// <summary>
/// Child-side resolution of parent-supplied SubFlow overrides. Resolved on the instance's OWN state
/// (the caller passes <c>CurrentState</c> / the entered state, never <c>EffectiveState</c>): a leaf
/// applies its own parent's stamp and nothing above it, so an override reaches exactly one hop.
/// </summary>
public static class InstanceSubFlowOverrideExtensions
{
    /// <summary>
    /// The one answer to "which long-poll is in force here". Every reader — the pipeline arm, the
    /// interaction gate, the state body — calls this; reading <c>State.LongPoll*</c> directly would let
    /// the window the job is armed with and the window the client is told diverge.
    /// </summary>
    public static EffectiveLongPollResolution ResolveEffectiveLongPoll(this Instance instance, State? state)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var own = state?.Interaction?.LongPoll;
        var states = SubFlowOverrideStamp.ReadStates(instance.ExtraProperties, out var malformed);
        var stateOverride = state is null ? null : states?.GetValueOrDefault(state.Key)?.Interaction?.LongPoll;
        var overridePresent = stateOverride is { IsEmpty: false };

        if (own is null)
        {
            // An override tunes a long-poll; it never creates one.
            return new EffectiveLongPollResolution(null, overridePresent, overridePresent, false, malformed);
        }

        var ruleArm = own.Rule is not null;
        var rolesOverrideIgnored = ruleArm && stateOverride?.Roles is not null;
        IReadOnlyCollection<RoleGrant> roles = !ruleArm && stateOverride?.Roles is { } overrideRoles
            ? overrideRoles
            : own.Roles;
        var seconds = stateOverride?.FallbackTimeoutSeconds
                      ?? own.FallbackTimeoutSeconds
                      ?? LongPollInteraction.DefaultFallbackTimeoutSeconds;

        return new EffectiveLongPollResolution(
            new EffectiveLongPoll(own.Terminate, seconds, roles, own.Rule),
            overridePresent,
            false,
            rolesOverrideIgnored,
            malformed);
    }

    /// <summary>
    /// The replacement for the view the child's own rules selected, or null. A transition view is
    /// looked up under <c>transitions[transitionKey].views</c>, a state view under
    /// <c>states[stateKey].views</c>; the two never fall through to each other.
    /// </summary>
    public static Reference? ResolveViewOverride(
        this Instance instance,
        string? stateKey,
        string? transitionKey,
        string viewKey)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (!string.IsNullOrWhiteSpace(transitionKey))
        {
            return SubFlowOverrideStamp.ReadTransitions(instance.ExtraProperties, out _)
                ?.GetValueOrDefault(transitionKey)?.Views?.GetValueOrDefault(viewKey);
        }

        if (string.IsNullOrEmpty(stateKey))
            return null;

        return SubFlowOverrideStamp.ReadStates(instance.ExtraProperties, out _)
            ?.GetValueOrDefault(stateKey)?.Views?.GetValueOrDefault(viewKey);
    }
}
