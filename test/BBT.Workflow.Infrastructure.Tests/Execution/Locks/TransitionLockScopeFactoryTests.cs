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
    public async Task AcquireAsync_WhenChainAlreadyHoldsKey_ShouldReturnReentrantScopeWithoutDistributedAcquire()
    {
        // Simulates the sync subflow completion callback: the parent pipeline higher up in
        // this async chain already holds the parent lock, so re-acquisition must succeed
        // without touching the distributed lock service (which would reject same-key acquire).
        const string key = "vnext:bank:parent-flow:reentrant";
        ChainLockRegistry.Register(key);
        _lockService.TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IDistributedLockHandle?)null);

        await using var scope = await CreateFactory().AcquireAsync(key);

        scope.IsAcquired.ShouldBeTrue();
        scope.LockKey.ShouldBe(key);
        await _lockService.DidNotReceive()
            .TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AcquireAsync_ReentrantScope_ShouldReportExtendAsSucceeded()
    {
        // The outer holder owns the real lease; a reentrant scope must not abort chains
        // that opt into between-hop lease extension.
        const string key = "vnext:bank:parent-flow:reentrant-extend";
        ChainLockRegistry.Register(key);

        await using var scope = await CreateFactory().AcquireAsync(key);

        (await scope.ExtendAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task AcquireAsync_ReentrantScope_DisposeShouldNotReleaseOuterLock()
    {
        const string key = "vnext:bank:parent-flow:reentrant-dispose";
        ChainLockRegistry.Register(key);

        var scope = await CreateFactory().AcquireAsync(key);
        await scope.DisposeAsync();

        // No handle was created, so nothing may be released on the lock service.
        await _lockService.DidNotReceive()
            .TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
