namespace BBT.Workflow.Definitions;

/// <summary>
/// Resolves the candidate transitions of an <see cref="StateType.AiAgent"/> state (issue #1105):
/// the state's automatic transitions without a rule, minus the decision's reserved exits
/// (<c>fallback</c>, <c>onApprovalRequest</c>, <c>onExhausted</c>) and <c>decision.exclude</c>.
/// An automatic transition WITH a rule is never a candidate: it keeps its rule and is evaluated by
/// the automatic-transition step as usual (mixed mode).
/// </summary>
/// <remarks>
/// Only the state's own transitions count, the same set <see cref="State.AutoTransitions"/> evaluates;
/// shared transitions never run as automatic transitions.
/// </remarks>
public static class AgentCandidateSet
{
    /// <summary>Returns the candidate transitions in evaluation order.</summary>
    public static IReadOnlyList<Transition> Resolve(State state)
    {
        var decision = state.Agent?.Decision;
        if (decision is null)
            return [];

        var excluded = new HashSet<string>(decision.ReservedKeys, StringComparer.Ordinal);
        foreach (var key in decision.Exclude ?? [])
            excluded.Add(key);

        return state.AutoTransitions
            .Where(t => t.Rule is null &&
                        t.TriggerKind != TransitionKind.DefaultAutoTransition &&
                        !excluded.Contains(t.Key))
            .ToList();
    }

    /// <summary>
    /// True when <paramref name="transition"/> is decided by the agent on <paramref name="state"/>:
    /// a rule-less candidate or a reserved exit.
    /// </summary>
    public static bool IsAgentDecided(State state, Transition transition)
    {
        if (state.StateType != StateType.AiAgent || state.Agent?.Decision is not { } decision)
            return false;
        if (transition.TriggerType != TriggerType.Automatic || transition.Rule is not null)
            return false;
        return true;
    }
}
