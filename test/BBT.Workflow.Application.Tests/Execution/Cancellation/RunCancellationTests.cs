using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Execution.Cancellation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace BBT.Workflow.Application.Tests.Execution.Cancellation;

/// <summary>
/// Pins the run-token contract of <see cref="RunCancellation"/> (vnext#1258): the caller's token
/// cannot cancel a run; only host shutdown or the TransitionJobTimeoutSeconds budget can.
/// </summary>
public sealed class RunCancellationTests : IDisposable
{
    private readonly CancellationTokenSource stoppingCts = new();
    private readonly FakeTimeProvider time = new();
    private readonly RunCancellation sut;

    public RunCancellationTests()
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(stoppingCts.Token);
        sut = new RunCancellation(
            lifetime,
            MsOptions.Create(new WorkflowExecutionOptions { TransitionJobTimeoutSeconds = 300 }),
            time);
    }

    public void Dispose() => stoppingCts.Dispose();

    [Fact]
    public void Begin_WhenCallerTokenCancelled_TokenStaysLive()
    {
        using var cts = new CancellationTokenSource();
        using var lease = sut.Begin(cts.Token);

        cts.Cancel();

        lease.Token.IsCancellationRequested.ShouldBeFalse();
        lease.CallerAborted.ShouldBeTrue();
    }

    [Fact]
    public void Begin_WhenCallerTokenLive_CallerAbortedIsFalse()
    {
        using var cts = new CancellationTokenSource();
        using var lease = sut.Begin(cts.Token);

        lease.CallerAborted.ShouldBeFalse();
    }

    [Fact]
    public void Begin_WhenApplicationStopping_CancelsToken()
    {
        using var lease = sut.Begin(default);

        stoppingCts.Cancel();

        lease.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Begin_AfterTransitionJobTimeoutBudget_CancelsToken()
    {
        using var lease = sut.Begin(default);

        time.Advance(TimeSpan.FromSeconds(299));
        lease.Token.IsCancellationRequested.ShouldBeFalse();

        time.Advance(TimeSpan.FromSeconds(2));
        lease.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Begin_WhenNestedInsideOpenLease_ReturnsOuterToken()
    {
        using var outer = sut.Begin(default);
        using var inner = sut.Begin(default);

        inner.Token.ShouldBe(outer.Token);

        inner.Dispose();
        outer.Token.IsCancellationRequested.ShouldBeFalse();
        // The outer lease stays the open one after a nested lease is disposed.
        using var again = sut.Begin(default);
        again.Token.ShouldBe(outer.Token);
    }

    [Fact]
    public void Begin_AfterOuterLeaseDisposed_StartsFreshLease()
    {
        CancellationToken first;
        using (var a = sut.Begin(default))
            first = a.Token;

        using var b = sut.Begin(default);

        b.Token.ShouldNotBe(first);
    }

    [Fact]
    public async Task Begin_InsideAsyncCallee_DoesNotLeakLeaseBackToCaller()
    {
        CancellationToken inner = default;
        await Task.Run(async () =>
        {
            await Task.Yield();
            var lease = sut.Begin(default);
            inner = lease.Token;
            // Intentionally left open: an async-local set in a callee must not flow back.
        });

        using var outer = sut.Begin(default);
        outer.Token.ShouldNotBe(inner);
    }
}
