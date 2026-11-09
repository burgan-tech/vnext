using BBT.Aether.Domain.Entities;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances.Agents;

/// <summary>Who produced the selected transition.</summary>
public enum AgentDecisionSource : short
{
    /// <summary>The model chose.</summary>
    Model = 1,

    /// <summary>A transition rule chose.</summary>
    Rules = 2,

    /// <summary>A decision script chose.</summary>
    Script = 3,

    /// <summary>The run could not decide; the fallback was taken.</summary>
    Fallback = 4,

    /// <summary><c>policy.maxEntries</c> was reached.</summary>
    Exhausted = 5,

    /// <summary>A tool asked for approval.</summary>
    ApprovalRequest = 6,

    /// <summary>The agent feature is disabled in this environment.</summary>
    Disabled = 7
}

/// <summary>The decision of an agent run (table <c>InstanceAgentDecisions</c>, one per run).</summary>
public sealed class InstanceAgentDecision : Entity<Guid>
{
    private InstanceAgentDecision()
    {
    }

    /// <summary>Creates a decision row.</summary>
    public InstanceAgentDecision(Guid id) : base(id)
    {
    }

    /// <summary>Owning run.</summary>
    public Guid RunId { get; set; }

    /// <summary>Owning instance.</summary>
    public Guid InstanceId { get; set; }

    /// <summary>The agent state.</summary>
    public string State { get; set; } = default!;

    /// <summary>Authored mode.</summary>
    public AgentDecisionMode Mode { get; set; }

    /// <summary>Who decided.</summary>
    public AgentDecisionSource Source { get; set; }

    /// <summary>The transition taken; for rules mode written by the automatic-transition step.</summary>
    public string? SelectedTransition { get; set; }

    /// <summary>Why.</summary>
    public string? Reason { get; set; }

    /// <summary>The candidate set the run saw: <c>[{ key, when, whenSource }]</c>.</summary>
    public string Candidates { get; set; } = "[]";

    /// <summary>Whether the fallback was taken.</summary>
    public bool IsFallback { get; set; }

    /// <summary>Row creation time.</summary>
    public DateTime CreatedAt { get; set; }
}
