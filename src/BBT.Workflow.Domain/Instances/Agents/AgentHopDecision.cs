using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances.Agents;

/// <summary>
/// The in-hop decision of an agent run, carried from the agent step to the automatic-transition step.
/// </summary>
/// <param name="Mode">Authored decision mode.</param>
/// <param name="Selected">The selected candidate (model/script) or the fallback; null in rules mode until a rule wins.</param>
/// <param name="Source">Who produced <paramref name="Selected"/>.</param>
/// <param name="CandidateKeys">The candidate keys the run saw.</param>
/// <param name="ReservedKeys">Fallback, onApprovalRequest and onExhausted keys.</param>
/// <param name="Fallback">The fallback key.</param>
/// <param name="DecisionRow">The decision row, still pending in the unit of work (rules mode writes back to it).</param>
public sealed record AgentHopDecision(
    AgentDecisionMode Mode,
    string? Selected,
    AgentDecisionSource Source,
    IReadOnlySet<string> CandidateKeys,
    IReadOnlySet<string> ReservedKeys,
    string Fallback,
    InstanceAgentDecision? DecisionRow)
{
    /// <summary>True when the run's outcome is a reserved exit (fallback, exhausted, approval).</summary>
    public bool IsReservedExit => Selected is not null && ReservedKeys.Contains(Selected);
}
