using BBT.Aether.Uow;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Runs a routed gateway's local branch for a co-hosted domain: as that domain
/// (<see cref="LocalDomainCall"/>), and — when it lives in a different database than the caller — in a
/// unit-of-work root of its own.
/// </summary>
/// <remarks>
/// One Aether unit-of-work root holds one connection and caches contexts by (type, schema). A local
/// read for a dedicated-database domain inside the caller's root would therefore reuse the caller's
/// connection — the wrong database, silently, since a dedicated domain's schemas carry no prefix. A
/// detached, non-transactional root gives the call its own connection. Same database (single domain,
/// shared-database pool): no new root, exactly the previous behaviour.
/// </remarks>
public interface ICoHostedDatabaseBoundary
{
    /// <summary>Starts <paramref name="call"/> as <paramref name="targetDomain"/>, in a new root when the database changes.</summary>
    Task<TResult> RunAsync<TResult>(string? targetDomain, Func<Task<TResult>> call);
}

/// <inheritdoc />
public sealed class CoHostedDatabaseBoundary(IDomainDatabaseResolver resolver, IUnitOfWorkManager unitOfWorkManager)
    : ICoHostedDatabaseBoundary
{
    /// <inheritdoc />
    public Task<TResult> RunAsync<TResult>(string? targetDomain, Func<Task<TResult>> call)
    {
        // Compared before the scope switches: the caller's database against the target's.
        if (string.IsNullOrWhiteSpace(targetDomain) || resolver.SameDatabase(null, targetDomain))
        {
            return LocalDomainCall.Run(targetDomain, call);
        }

        return LocalDomainCall.Run(targetDomain, () => InOwnRootAsync(call));
    }

    private async Task<TResult> InOwnRootAsync<TResult>(Func<Task<TResult>> call)
    {
        await using var uow = unitOfWorkManager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = false
        });

        var result = await call();
        await uow.CommitAsync();
        return result;
    }
}
