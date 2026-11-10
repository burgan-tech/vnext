using BBT.Aether.Domain.Entities;

namespace BBT.Workflow.Instances.Agents;

/// <summary>Outcome of an agent run.</summary>
public enum AgentRunStatus : short
{
    /// <summary>The run is in progress (never committed in Faz 1; the row is written once).</summary>
    Running = 1,

    /// <summary>The model produced a final answer.</summary>
    Completed = 2,

    /// <summary>A tool asked for human approval.</summary>
    ApprovalRequested = 3,

    /// <summary>The provider or a script failed.</summary>
    Failed = 4,

    /// <summary>The run deadline elapsed.</summary>
    Timeout = 5,

    /// <summary>A token, iteration or tool-call budget was exceeded.</summary>
    BudgetExceeded = 6
}

/// <summary>
/// One agent run on an <c>AiAgent</c> state entry (table <c>InstanceAgentRuns</c>, issue #1105).
/// Written once, in the hop's unit of work; never read by a hot path (state function, instance GET/list).
/// </summary>
public sealed class InstanceAgentRun : Entity<Guid>
{
    private InstanceAgentRun()
    {
    }

    /// <summary>Creates a run row.</summary>
    public InstanceAgentRun(Guid id) : base(id)
    {
    }

    /// <summary>Owning instance.</summary>
    public Guid InstanceId { get; set; }

    /// <summary>The transition record the run belongs to.</summary>
    public Guid? TransitionId { get; set; }

    /// <summary>The agent state.</summary>
    public string State { get; set; } = default!;

    /// <summary>SHA-256 of <c>{transitionId}:{state}:{entryNo}</c>: one row per state entry.</summary>
    public string ExecutionKey { get; set; } = default!;

    /// <summary>Which entry to this state this run served.</summary>
    public int EntryNo { get; set; }

    /// <summary>
    /// Participant name of a multi-agent state's auxiliary run (a sequential/concurrent participant or the
    /// handoff router); null for the run that produced the state's output and decision.
    /// </summary>
    public string? Participant { get; set; }

    /// <summary>Resolved agent key.</summary>
    public string AgentKey { get; set; } = default!;

    /// <summary>Resolved agent domain.</summary>
    public string AgentDomain { get; set; } = default!;

    /// <summary>Resolved agent version.</summary>
    public string AgentVersion { get; set; } = default!;

    /// <summary>Agent runtime key.</summary>
    public string RuntimeKey { get; set; } = default!;

    /// <summary>Provider profile name.</summary>
    public string ProviderKey { get; set; } = default!;

    /// <summary>Model reported by the provider.</summary>
    public string? Model { get; set; }

    /// <summary>Run outcome.</summary>
    public AgentRunStatus Status { get; set; }

    /// <summary>Why the run did not complete.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Provider attempts including retries.</summary>
    public short Attempts { get; set; }

    /// <summary>Provider response id.</summary>
    public string? ProviderResponseId { get; set; }

    /// <summary>Provider finish reason.</summary>
    public string? FinishReason { get; set; }

    /// <summary>Start.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>End.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Elapsed milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Model round trips.</summary>
    public int Iterations { get; set; }

    /// <summary>Tool calls made.</summary>
    public int ToolCallCount { get; set; }

    /// <summary>Input tokens.</summary>
    public int PromptTokens { get; set; }

    /// <summary>Output tokens.</summary>
    public int CompletionTokens { get; set; }

    /// <summary>All tokens.</summary>
    public int TotalTokens { get; set; }

    /// <summary>Redacted request summary (instructions hash, prompt, schema, tool names, candidates).</summary>
    public string? Request { get; set; }

    /// <summary>Structured output.</summary>
    public string? Response { get; set; }

    /// <summary>Gzip-compressed transcript per <c>audit.transcript</c>.</summary>
    public byte[]? Transcript { get; set; }

    /// <summary>The session scope the run read and wrote.</summary>
    public string? SessionScopeKey { get; set; }

    /// <summary>Whether the run resumed an existing session.</summary>
    public bool SessionResumed { get; set; }

    /// <summary>Row creation time (application clock).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Builds the execution key of a state entry.</summary>
    public static string CreateExecutionKey(Guid? transitionId, string state, int entryNo, string? participant = null)
    {
        var raw = $"{transitionId?.ToString("N") ?? "none"}:{state}:{entryNo}" + (participant is null ? string.Empty : $":{participant}");
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
    }
}
