using BBT.Aether.Results;

namespace BBT.Workflow.Execution.Transactions;

/// <summary>
/// Runs a short, transactional unit of DB work — the building block of the per-operation
/// transactional model (invariants INV-1 and INV-3). Opens a <c>RequiresNew</c>,
/// <c>IsTransactional=true</c> unit of work, executes the supplied work, and commits on success
/// (business writes + buffered domain events atomically, via the standard outbox path). On failure
/// or exception the unit of work is disposed without commit → rollback.
/// <para>
/// The work MUST NOT perform any remote/external call (INV-2): a transaction may not span a remote
/// call. Any instance reload (INV-4) is performed inside <paramref name="work"/> so the caller
/// operates on an entity tracked by this unit of work's fresh context.
/// </para>
/// </summary>
public interface ITransactionalWorkUnit
{
    /// <summary>Runs transactional work returning a value; commits only on success.</summary>
    Task<Result<T>> RunAsync<T>(
        Func<CancellationToken, Task<Result<T>>> work,
        CancellationToken cancellationToken = default);

    /// <summary>Runs transactional work with no return value; commits only on success.</summary>
    Task<Result> RunAsync(
        Func<CancellationToken, Task<Result>> work,
        CancellationToken cancellationToken = default);
}
