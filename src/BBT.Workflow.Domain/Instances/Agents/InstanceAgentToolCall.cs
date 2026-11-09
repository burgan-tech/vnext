using BBT.Aether.Domain.Entities;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances.Agents;

/// <summary>Outcome of one tool call.</summary>
public enum AgentToolCallStatus : short
{
    /// <summary>The tool returned.</summary>
    Completed = 1,

    /// <summary>The tool failed.</summary>
    Failed = 2,

    /// <summary>The tool timed out.</summary>
    Timeout = 3,

    /// <summary>The arguments did not match the tool schema, or a budget refused the call.</summary>
    Rejected = 4,

    /// <summary>Waiting for approval (Faz 2).</summary>
    ApprovalPending = 5,

    /// <summary>Approved (Faz 2).</summary>
    Approved = 6,

    /// <summary>Denied (Faz 2).</summary>
    Denied = 7,

    /// <summary>Not offered to the model (role, exclude).</summary>
    Skipped = 8
}

/// <summary>One tool call of an agent run (table <c>InstanceAgentToolCalls</c>).</summary>
public sealed class InstanceAgentToolCall : Entity<Guid>
{
    private InstanceAgentToolCall()
    {
    }

    /// <summary>Creates a tool-call row.</summary>
    public InstanceAgentToolCall(Guid id) : base(id)
    {
    }

    /// <summary>Owning run.</summary>
    public Guid RunId { get; set; }

    /// <summary>Owning instance.</summary>
    public Guid InstanceId { get; set; }

    /// <summary>Call order inside the run.</summary>
    public int Sequence { get; set; }

    /// <summary>Model round trip the call belongs to.</summary>
    public int Iteration { get; set; }

    /// <summary>The model's call id.</summary>
    public string CallId { get; set; } = default!;

    /// <summary>Tool name as the model saw it.</summary>
    public string Name { get; set; } = default!;

    /// <summary>Tool kind.</summary>
    public AgentToolKind Kind { get; set; }

    /// <summary><c>sys-tasks/{domain}/{key}@{version}</c> or null for an inline script.</summary>
    public string? BindingRef { get; set; }

    /// <summary>SHA-256 of <c>{RunId}:{CallId}</c>; sent to HTTP tools as <c>Idempotency-Key</c>.</summary>
    public string IdempotencyKey { get; set; } = default!;

    /// <summary>Redacted arguments.</summary>
    public string? Arguments { get; set; }

    /// <summary>Result, when <c>audit.persistToolResults</c>.</summary>
    public string? Result { get; set; }

    /// <summary>Outcome.</summary>
    public AgentToolCallStatus Status { get; set; }

    /// <summary>Error text.</summary>
    public string? Error { get; set; }

    /// <summary>Whether the tool requires approval.</summary>
    public bool RequiresApproval { get; set; }

    /// <summary>The task journal row of a task or MCP tool.</summary>
    public Guid? InstanceTaskId { get; set; }

    /// <summary>Start.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>End.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Elapsed milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Builds the idempotency key of a call.</summary>
    public static string CreateIdempotencyKey(Guid runId, string callId) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{runId:N}:{callId}")));
}
