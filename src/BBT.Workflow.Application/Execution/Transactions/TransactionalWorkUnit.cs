using BBT.Aether.Results;
using BBT.Aether.Uow;

namespace BBT.Workflow.Execution.Transactions;

/// <summary>
/// Default <see cref="ITransactionalWorkUnit"/>. Each call opens a fresh
/// <c>RequiresNew, IsTransactional=true</c> unit of work so schema resolution
/// (<c>SET LOCAL search_path</c> under TransactionLocal) has an active transaction and the work
/// is atomic (business + outbox). The unit of work is short by contract — no remote calls inside.
/// </summary>
public sealed class TransactionalWorkUnit(IUnitOfWorkManager uowManager) : ITransactionalWorkUnit
{
    /// <inheritdoc />
    public async Task<Result<T>> RunAsync<T>(
        Func<CancellationToken, Task<Result<T>>> work,
        CancellationToken cancellationToken = default)
    {
        await using var uow = uowManager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = true
        });

        var result = await work(cancellationToken);
        if (result.IsSuccess)
            await uow.CommitAsync(cancellationToken);

        // On failure the UoW is disposed without commit → rollback.
        return result;
    }

    /// <inheritdoc />
    public async Task<Result> RunAsync(
        Func<CancellationToken, Task<Result>> work,
        CancellationToken cancellationToken = default)
    {
        await using var uow = uowManager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = true
        });

        var result = await work(cancellationToken);
        if (result.IsSuccess)
            await uow.CommitAsync(cancellationToken);

        return result;
    }
}
