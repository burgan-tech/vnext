namespace BBT.Workflow.Infrastructure.Execution.Locks;

/// <summary>
/// Persistence row for the platform-owned distributed lock lease store
/// (<see cref="NpgsqlDistributedLockService"/>). Defined on <c>MessagingDbContext</c>
/// (<c>sys_queues</c> schema) so the table is created code-first by an EF Core migration
/// applied at deploy time — consistent with the outbox/inbox/background-job tables — rather
/// than by runtime DDL.
/// <para>
/// The lock service reads/writes this table with raw ADO.NET on dedicated connections
/// (outside any ambient Unit of Work) because a lock's lifetime is independent of business
/// transactions; this entity exists only to own the schema, not to drive runtime operations.
/// </para>
/// </summary>
public sealed class DistributedLockRecord
{
    /// <summary>Lock resource key (primary key).</summary>
    public string Key { get; set; } = default!;

    /// <summary>Unique owner identifier of the current lease holder.</summary>
    public string Owner { get; set; } = default!;

    /// <summary>Monotonic fencing token, incremented on every ownership change of this key.</summary>
    public long Fence { get; set; }

    /// <summary>Absolute expiry (UTC) of the current lease.</summary>
    public DateTime ExpiresAt { get; set; }
}
