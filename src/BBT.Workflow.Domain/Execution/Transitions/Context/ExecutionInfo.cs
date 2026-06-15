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

    /// <summary>Gets or sets whether this execution is triggered by a workflow timeout.</summary>
    public bool IsTimeoutTransition { get; set; }
}
