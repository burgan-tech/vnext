namespace BBT.Workflow.Instances.Agents;

/// <summary>
/// Write side of the agent run journal (issue #1105). Rows are ADDED to the hop's unit of work and
/// reach the database with its next save or its commit, as one batch; nothing here saves on its own.
/// </summary>
public interface IAgentRunJournal
{
    /// <summary>Counts previous runs of <paramref name="state"/> for the instance (entry number, <c>maxEntries</c>).</summary>
    Task<int> CountRunsAsync(Guid instanceId, string state, CancellationToken cancellationToken = default);

    /// <summary>Adds a run with its tool calls and decision to the unit of work.</summary>
    Task AddAsync(
        InstanceAgentRun run,
        IReadOnlyList<InstanceAgentToolCall> toolCalls,
        InstanceAgentDecision? decision,
        CancellationToken cancellationToken = default);
}

/// <summary>A decrypted session as the agent step reads it.</summary>
/// <param name="ScopeKey">Scope key.</param>
/// <param name="AgentKey">Agent that wrote it.</param>
/// <param name="AgentVersion">Agent version that wrote it.</param>
/// <param name="InstructionsHash">Instructions hash it was written with.</param>
/// <param name="Messages">Serialized message list (UTF-8 JSON), or null for provider storage.</param>
/// <param name="ProviderConversationId">Provider conversation id.</param>
/// <param name="MessageCount">Messages kept.</param>
/// <param name="RowVersion">Concurrency token.</param>
public sealed record AgentSessionSnapshot(
    string ScopeKey,
    string AgentKey,
    string AgentVersion,
    string? InstructionsHash,
    byte[]? Messages,
    string? ProviderConversationId,
    int MessageCount,
    uint RowVersion);

/// <summary>Result of a session read.</summary>
/// <param name="Snapshot">The session, when one exists and could be read.</param>
/// <param name="Unreadable">Why an existing row could not be read; null otherwise.</param>
public sealed record AgentSessionReadResult(AgentSessionSnapshot? Snapshot, string? Unreadable);

/// <summary>What the agent step writes back.</summary>
/// <param name="ScopeKey">Scope key.</param>
/// <param name="Session">Row metadata (mode, storage, agent, owner, expiry).</param>
/// <param name="Messages">Serialized message list to encrypt, or null for provider storage.</param>
/// <param name="ExpectedRowVersion">Row version read before the run; null when no row existed.</param>
public sealed record AgentSessionWrite(
    string ScopeKey,
    AgentSession Session,
    byte[]? Messages,
    uint? ExpectedRowVersion);

/// <summary>
/// Stores agent conversations (table <c>AgentSessions</c>). Reads are one primary-key lookup; a write is
/// one upsert statement guarded by the row version. Content is compressed, then AES-256-GCM encrypted
/// with a fresh random key stored on the row itself; deleting the row (or the owning instance, which
/// cascades to its <c>i:*</c> rows) destroys the key with the content. Rows expire <c>retentionDays</c>
/// after their last write.
/// </summary>
public interface IAgentSessionStore
{
    /// <summary>Reads and decrypts a session.</summary>
    Task<AgentSessionReadResult> FindAsync(string scopeKey, Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>Writes a session; false when another writer changed it since it was read.</summary>
    Task<bool> SaveAsync(AgentSessionWrite write, Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>Deletes up to <paramref name="batchSize"/> expired rows; returns how many were deleted.</summary>
    Task<int> SweepExpiredAsync(int batchSize, CancellationToken cancellationToken = default);
}

/// <summary>A run row as the read surface lists it (no request, response or transcript).</summary>
public sealed record AgentRunListItem(
    Guid Id,
    Guid? TransitionId,
    string State,
    int EntryNo,
    string AgentKey,
    string AgentVersion,
    string ProviderKey,
    string? Model,
    AgentRunStatus Status,
    string? FailureReason,
    short Attempts,
    DateTime StartedAt,
    DateTime? FinishedAt,
    long DurationMs,
    int Iterations,
    int ToolCallCount,
    int TotalTokens,
    string? SessionScopeKey,
    bool SessionResumed,
    string? SelectedTransition,
    AgentDecisionSource? DecisionSource,
    string? DecisionReason,
    string? Participant = null);

/// <summary>A tool call row as the read surface returns it.</summary>
public sealed record AgentToolCallItem(
    Guid Id,
    int Sequence,
    int Iteration,
    string CallId,
    string Name,
    short Kind,
    string? BindingRef,
    AgentToolCallStatus Status,
    string? Error,
    Guid? InstanceTaskId,
    DateTime StartedAt,
    long DurationMs);

/// <summary>Read side of the agent run journal; projections only, never entities.</summary>
public interface IAgentRunReadRepository
{
    /// <summary>Lists runs newest first.</summary>
    Task<IReadOnlyList<AgentRunListItem>> ListAsync(Guid instanceId, string? state, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>One run with its decision.</summary>
    Task<AgentRunListItem?> FindAsync(Guid instanceId, Guid runId, CancellationToken cancellationToken = default);

    /// <summary>The tool calls of a run in order.</summary>
    Task<IReadOnlyList<AgentToolCallItem>> ListToolCallsAsync(Guid runId, CancellationToken cancellationToken = default);
}
