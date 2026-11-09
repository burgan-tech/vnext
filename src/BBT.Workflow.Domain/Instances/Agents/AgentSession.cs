using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances.Agents;

/// <summary>
/// A persisted agent conversation (table <c>AgentSessions</c>, issue #1105). Keyed by
/// <see cref="ScopeKey"/>: <c>i:{instanceId}:{flow}[:{state}]</c> for instance-scoped memory,
/// <c>k:{domain}:{agentKey}:{sha256}</c> for cross-instance memory. The message list is stored
/// compressed and AES-256-GCM encrypted; it is never served by an API.
/// </summary>
public sealed class AgentSession
{
    /// <summary>Scope key (primary key).</summary>
    public string ScopeKey { get; set; } = default!;

    /// <summary>Memory scope.</summary>
    public AgentMemoryMode Mode { get; set; }

    /// <summary>Message store.</summary>
    public AgentMemoryStorage Storage { get; set; }

    /// <summary>Agent the session was written by.</summary>
    public string AgentKey { get; set; } = default!;

    /// <summary>Agent version the session was written by.</summary>
    public string AgentVersion { get; set; } = default!;

    /// <summary>SHA-256 of the instructions the session was written with.</summary>
    public string? InstructionsHash { get; set; }

    /// <summary>Encrypted, compressed message list (<see cref="AgentMemoryStorage.Local"/>).</summary>
    public byte[]? SessionData { get; set; }

    /// <summary>Provider conversation id (<see cref="AgentMemoryStorage.Provider"/>).</summary>
    public string? ProviderConversationId { get; set; }

    /// <summary>The row's own AES-256 key (rotated on every write). Never served, logged or traced.</summary>
    public byte[]? EncryptionKey { get; set; }

    /// <summary>Messages kept.</summary>
    public int MessageCount { get; set; }

    /// <summary>Stored size.</summary>
    public int SizeBytes { get; set; }

    /// <summary>Owning instance of an <c>i:*</c> row; null for <c>k:*</c>.</summary>
    public Guid? OwnerInstanceId { get; set; }

    /// <summary>Last instance that wrote the row.</summary>
    public Guid LastInstanceId { get; set; }

    /// <summary>Last run that wrote the row.</summary>
    public Guid LastRunId { get; set; }

    /// <summary>Optimistic concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint RowVersion { get; set; }

    /// <summary>Last write.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>When the row may be swept.</summary>
    public DateTime? ExpiresAt { get; set; }
}
