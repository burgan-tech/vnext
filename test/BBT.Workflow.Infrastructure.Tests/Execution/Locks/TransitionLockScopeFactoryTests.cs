using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedLock;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Infrastructure.Execution.Locks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Locks;

/// <summary>
/// Unit tests for <see cref="TransitionLockScopeFactory"/> — distributed acquisition
/// and chain-reentrant acquisition via <see cref="ChainLockRegistry"/>.
/// </summary>
public sealed class TransitionLockScopeFactoryTests
{
    private readonly IDistributedLockService _lockService = Substitute.For<IDistributedLockService>();

    private TransitionLockScopeFactory CreateFactory() => new(
        _lockService,
        Options.Create(new WorkflowExecutionOptions()),
        Substitute.For<ILogger<TransitionLockScopeFactory>>());

    [Fact]
    public async Task AcquireAsync_WhenKeyNotHeldByChain_ShouldDelegateToDistributedLockService()
    {
        const string key = "vnext:bank:parent-flow:delegate";
        var handle = Substitute.For<IDistributedLockHandle>();
        _lockService.TryAcquireLockAsync(key, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(handle);

        await using var scope = await CreateFactory().AcquireAsync(key);

        scope.IsAcquired.ShouldBeTrue();
        scope.LockKey.ShouldBe(key);
        await _lockService.Received(1)
            .TryAcquireLockAsync(key, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcquireAsync_WhenKeyNotHeldAndServiceReturnsNull_ShouldReturnNotAcquired()
    {
        const string key = "vnext:bank:parent-flow:contended";
        _lockService.TryAcquireLockAsync(key, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IDistributedLockHandle?)null);

        await using var scope = await CreateFactory().AcquireAsync(key);

        scope.IsAcquired.ShouldBeFalse();
    }

    [Fact]
    public async Task AcquireAsync_WithoutWaitPolicy_ShouldNotRetry()
    {
        // The transition pipeline depends on fail-fast: a busy instance must surface immediately.
        const string key = "vnext:bank:parent-flow:failfast";
        _lockService.TryAcquireLockAsync(key, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IDistributedLockHandle?)null);

        await using var scope = await CreateFactory().AcquireAsync(key);

        scope.IsAcquired.ShouldBeFalse();
        await _lockService.Received(1)
            .TryAcquireLockAsync(key, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcquireAsync_WithWaitPolicy_ShouldRetryUntilLockIsReleased()
    {
        // A duplicate at-least-once delivery collides with the in-flight original; the original's
        // critical section is one short transaction, so waiting it out must succeed.
        const string key = "vnext:bank:parent-flow:contended-wait";
        var handle = Substitute.For<IDistributedLockHandle>();
        var attempts = 0;
        _lockService.TryAcquireLockAsync(key, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++attempts < 3 ? null : handle);

        await using var scope = await CreateFactory().AcquireAsync(
            key,
            new LockAcquireWait(4, TimeSpan.FromMilliseconds(1)));

        scope.IsAcquired.ShouldBeTrue();
        attempts.ShouldBe(3);
    }

    [Fact]
    public async Task AcquireAsync_WithWaitPolicy_ShouldGiveUpAfterMaxAttempts()
    {
        const string key = "vnext:bank:parent-flow:contended-exhausted";
        _lockService.TryAcquireLockAsync(key, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IDistributedLockHandle?)null);

        await using var scope = await CreateFactory().AcquireAsync(
            key,
            new LockAcquireWait(3, TimeSpan.FromMilliseconds(1)));

        scope.IsAcquired.ShouldBeFalse();
        await _lockService.Received(3)
            .TryAcquireLockAsync(key, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LegacyImplementation_WithoutWaitOverload_ShouldStillSatisfyTheInterface()
    {
        // BBT.Workflow.Domain ships as a NuGet package, so an implementation written before the
        // wait overload existed must keep compiling and keep its fail-fast behaviour. This test
        // exists to fail at COMPILE time if the overload ever loses its default implementation.
        ITransitionLockScopeFactory legacy = new LegacyLockScopeFactory();

        var withoutWait = await legacy.AcquireAsync("vnext:bank:parent-flow:legacy");
        var withWait = await legacy.AcquireAsync(
            "vnext:bank:parent-flow:legacy",
            new LockAcquireWait(5, TimeSpan.FromMilliseconds(50)));

        withoutWait.IsAcquired.ShouldBeTrue();
        withWait.IsAcquired.ShouldBeTrue();
        ((LegacyLockScopeFactory)legacy).Calls.ShouldBe(2);
    }

    /// <summary>
    /// Implements only the original member — deliberately does NOT override the wait overload.
    /// </summary>
    private sealed class LegacyLockScopeFactory : ITransitionLockScopeFactory
    {
        public int Calls { get; private set; }

        public Task<ITransitionLockScope> AcquireAsync(
            string lockKey,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<ITransitionLockScope>(new StubScope(lockKey));
        }

        private sealed class StubScope(string lockKey) : ITransitionLockScope
        {
            public bool IsAcquired => true;
            public string LockKey => lockKey;
            public Task<bool> ExtendAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

}
