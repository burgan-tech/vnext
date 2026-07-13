using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Transactions;

/// <summary>
/// Unit tests for <see cref="TransactionalWorkUnit"/>: opens a RequiresNew + transactional unit of
/// work, commits only on success, and leaves failure to rollback (no commit).
/// </summary>
public class TransactionalWorkUnitTests
{
    private static (TransactionalWorkUnit Sut, IUnitOfWork Uow, IUnitOfWorkManager Mgr) Create()
    {
        var uow = Substitute.For<IUnitOfWork>();
        var mgr = Substitute.For<IUnitOfWorkManager>();
        mgr.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(uow);
        return (new TransactionalWorkUnit(mgr), uow, mgr);
    }

    [Fact]
    public async Task Success_OpensTransactionalRequiresNew_AndCommits()
    {
        var (sut, uow, mgr) = Create();

        var result = await sut.RunAsync(_ => Task.FromResult(Result.Ok()));

        result.IsSuccess.ShouldBeTrue();
        mgr.Received(1).Begin(Arg.Is<UnitOfWorkOptions>(o =>
            o.IsTransactional && o.Scope == UnitOfWorkScopeOption.RequiresNew));
        await uow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failure_DoesNotCommit()
    {
        var (sut, uow, _) = Create();

        var result = await sut.RunAsync(_ => Task.FromResult(Result.Fail(Error.Failure("X", "boom"))));

        result.IsSuccess.ShouldBeFalse();
        await uow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Generic_Success_CommitsAndReturnsValue()
    {
        var (sut, uow, _) = Create();

        var result = await sut.RunAsync(_ => Task.FromResult(Result<int>.Ok(42)));

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
        await uow.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Generic_Failure_DoesNotCommit()
    {
        var (sut, uow, _) = Create();

        var result = await sut.RunAsync(_ => Task.FromResult(Result<int>.Fail(Error.Failure("X", "boom"))));

        result.IsSuccess.ShouldBeFalse();
        await uow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }
}
