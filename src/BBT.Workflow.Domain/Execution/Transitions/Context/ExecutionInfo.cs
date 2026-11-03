using BBT.Workflow.Instances;

namespace BBT.Workflow.Execution;

/// <summary>
/// Execution information for tracking re-entry scenarios.
/// </summary>
public sealed class ExecutionInfo
{
    /// <summary>Gets or sets the execution chain identifier.</summary>
    public string ExecutionChainId { get; set; } = default!;
    
    /// <summary>Gets or sets the depth in the execution chain.</summary>
    public int ChainDepth { get; set; }

    /// <summary>Gets or sets the lifecycle order.</summary>
    public int? ResumeFrom { get; set; }
    
    /// <summary>Gets or sets whether this execution is resuming from a SubFlow completion.</summary>
    public bool IsSubFlowResume { get; set; }

    /// <summary>Gets or sets the completing SubFlow instance id when this execution
    /// is resuming from a SubFlow completion. Scopes the resume lock per sub-instance.</summary>
    public Guid? SubFlowResumeInstanceId { get; set; }

    /// <summary>Gets or sets the terminal outcome the completing sub item reached, when this
    /// execution resumes the parent from that outcome. Set by the completion relay so the resume
    /// hop can stamp the correlation's <c>SettledAt</c> marker in its own unit of work instead of a
    /// separate one after the resume; null leaves the marker to the caller.</summary>
    public SubItemTerminalOutcome? SubFlowResumeTerminalOutcome { get; set; }

    /// <summary>Gets or sets the instant recorded as the correlation's <c>SettledAt</c> when
    /// <see cref="SubFlowResumeTerminalOutcome"/> is set (the terminal event's own timestamp).</summary>
    public DateTime? SubFlowResumeSettledAt { get; set; }

    /// <summary>Gets or sets whether this execution is resuming from a long-poll acknowledge
    /// (client acknowledged the termination signal, or the fallback timeout fired).</summary>
    public bool IsLongPollAckResume { get; set; }

    /// <summary>Gets or sets whether this execution is triggered by a workflow timeout.</summary>
    public bool IsTimeoutTransition { get; set; }
}
