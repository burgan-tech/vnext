using BBT.Aether.DistributedLock;

namespace BBT.Workflow.Infrastructure.Execution.Locks;

/// <summary>
/// Explicit PostgreSQL-backed distributed-lock capability for consumers that must not use the
/// application's default Aether lock provider.
/// </summary>
public interface IPostgreSqlDistributedLockService : IDistributedLockService
{
}
