using System;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Transactions;

/// <summary>
/// Unit tests for <see cref="TransactionGuardrail"/> (INV-2 enforcement): a remote call while a
/// transactional unit of work is active is a violation. Off = no-op, Warn = log, Throw = raise.
/// </summary>
public class TransactionGuardrailTests
{
    private static IUnitOfWork Uow(bool transactional, bool completed = false, IUnitOfWork? outer = null)
    {
        var uow = Substitute.For<IUnitOfWork>();
        uow.Options.Returns(new UnitOfWorkOptions { IsTransactional = transactional });
        uow.IsCompleted.Returns(completed);
        uow.Outer.Returns(outer);
        return uow;
    }

    private static TransactionGuardrail Create(TransactionGuardrailMode mode, IUnitOfWork? current)
    {
        var mgr = Substitute.For<IUnitOfWorkManager>();
        mgr.Current.Returns(current);
        var opts = Options.Create(new WorkflowExecutionOptions { TransactionGuardrailMode = mode });
        return new TransactionGuardrail(mgr, opts, NullLogger<TransactionGuardrail>.Instance);
    }

    [Fact]
    public void Off_TransactionalActive_DoesNotThrow()
    {
        var g = Create(TransactionGuardrailMode.Off, Uow(transactional: true));
        Should.NotThrow(() => g.EnsureNoActiveTransaction("remote"));
    }

    [Fact]
    public void Throw_TransactionalActive_Throws()
    {
        var g = Create(TransactionGuardrailMode.Throw, Uow(transactional: true));
        Should.Throw<InvalidOperationException>(() => g.EnsureNoActiveTransaction("remote"));
    }

    [Fact]
    public void Throw_NonTransactionalCurrent_DoesNotThrow()
    {
        var g = Create(TransactionGuardrailMode.Throw, Uow(transactional: false));
        Should.NotThrow(() => g.EnsureNoActiveTransaction("remote"));
    }

    [Fact]
    public void Throw_NoAmbientUoW_DoesNotThrow()
    {
        var g = Create(TransactionGuardrailMode.Throw, current: null);
        Should.NotThrow(() => g.EnsureNoActiveTransaction("remote"));
    }

    [Fact]
    public void Throw_TransactionalAncestorInChain_Throws()
    {
        var outer = Uow(transactional: true);
        var current = Uow(transactional: false, outer: outer);
        var g = Create(TransactionGuardrailMode.Throw, current);
        Should.Throw<InvalidOperationException>(() => g.EnsureNoActiveTransaction("remote"));
    }

    [Fact]
    public void Throw_CompletedTransactional_DoesNotThrow()
    {
        // A committed/rolled-back transactional UoW no longer holds a connection → not a violation.
        var g = Create(TransactionGuardrailMode.Throw, Uow(transactional: true, completed: true));
        Should.NotThrow(() => g.EnsureNoActiveTransaction("remote"));
    }

    [Fact]
    public void Warn_TransactionalActive_DoesNotThrow()
    {
        var g = Create(TransactionGuardrailMode.Warn, Uow(transactional: true));
        Should.NotThrow(() => g.EnsureNoActiveTransaction("remote"));
    }
}
