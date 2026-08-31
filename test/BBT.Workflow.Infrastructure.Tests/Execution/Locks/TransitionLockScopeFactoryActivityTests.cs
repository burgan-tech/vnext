using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedLock;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Infrastructure.Execution.Locks;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Locks;

/// <summary>
/// Pins the <c>Lock.Acquire</c> span emitted around <see cref="TransitionLockScopeFactory.AcquireAsync"/>
/// (the long auto-chain-budget lock funnel) — added for symmetry with the <c>Lock.Release</c> span
/// that <see cref="TransitionLockScope.DisposeAsync"/> already emits for handle-bearing scopes
/// constructed by this same factory. Without this span, every successful acquisition on this funnel
/// produced an orphan <c>Lock.Release</c> with no matching <c>Lock.Acquire</c>.
/// </summary>
public sealed class TransitionLockScopeFactoryActivityTests : IDisposable
{
    private readonly List<ActivityListener> _listeners = new();

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            listener.Dispose();
        }

        Activity.Current = null;
    }

    private ActivityListener CreateListener(string sourceName, List<Activity> collected)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = collected.Add
        };
        ActivitySource.AddActivityListener(listener);
        _listeners.Add(listener);
        return listener;
    }

    private static TransitionLockScopeFactory CreateFactory(
        IDistributedLockService lockService,
        int transitionLockLeaseSeconds = 42) => new(
        lockService,
        Options.Create(new WorkflowExecutionOptions { TransitionLockLeaseSeconds = transitionLockLeaseSeconds }),
        Substitute.For<ILogger<TransitionLockScopeFactory>>());

    /// <summary>
    /// The <c>BBT.Workflow.Pipeline</c> ActivitySource is process-wide, and xUnit runs test
    /// classes in parallel by default — a listener here observes spans from every concurrently
    /// running test on that source, not just this test's own (confirmed: without the key filter,
    /// this suite intermittently observed spans from the sibling
    /// <c>InstanceStatusLockActivityTests</c> class, which uses the same source). Every assertion
    /// below matches on <see cref="TelemetryConstants.TagNames.LockKey"/> in addition to
    /// <c>DisplayName</c> so a concurrently running span never counts as this test's own.
    /// </summary>
    // The lock key is part of the span NAME (Lock.Acquire/{key}); see InstanceStatusLockActivityTests.
    private static bool IsSpan(Activity activity, string operationName, string lockKey) =>
        activity.DisplayName == $"{operationName}/{lockKey}" &&
        Equals(activity.GetTagItem(TelemetryConstants.TagNames.LockKey), lockKey);

    [Fact]
    public async Task AcquireAsync_WhenAcquiredOnFirstAttempt_EmitsLockAcquireSpan_Acquired()
    {
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Pipeline", collected);

        const string key = "vnext:bank:parent-flow:acquire-span";
        var handle = Substitute.For<IDistributedLockHandle>();
        var lockService = Substitute.For<IDistributedLockService>();
        lockService.TryAcquireLockAsync(key, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(handle);

        var factory = CreateFactory(lockService);

        await using var scope = await factory.AcquireAsync(key);

        scope.IsAcquired.ShouldBeTrue();

        var span = Assert.Single(collected, a => IsSpan(a, "Lock.Acquire", key));
        span.GetTagItem(TelemetryConstants.TagNames.LockAcquired).ShouldBe(true);
        span.GetTagItem(TelemetryConstants.TagNames.LockLeaseSeconds).ShouldBe(42);
        span.GetTagItem(TelemetryConstants.TagNames.LockKind).ShouldBe("chain");
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task AcquireAsync_WhenExhaustedAfterRetries_EmitsSingleLockAcquireSpan_NotAcquired_WithoutErrorStatus()
    {
        // One AcquireAsync call retries internally (LockAcquireWait); the span wraps the whole
        // logical acquire operation — not one span per low-level TryAcquireLockAsync call — so it
        // pairs 1:1 with the eventual Lock.Release (or, on exhaustion, with no release at all).
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Pipeline", collected);

        const string key = "vnext:bank:parent-flow:acquire-span-exhausted";
        var lockService = Substitute.For<IDistributedLockService>();
        lockService.TryAcquireLockAsync(key, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IDistributedLockHandle?)null);

        var factory = CreateFactory(lockService);

        await using var scope = await factory.AcquireAsync(
            key,
            new LockAcquireWait(3, TimeSpan.FromMilliseconds(1)));

        scope.IsAcquired.ShouldBeFalse();

        var acquireSpans = collected.Where(a => IsSpan(a, "Lock.Acquire", key)).ToList();
        acquireSpans.Count.ShouldBe(1); // one span per AcquireAsync call, despite 3 internal attempts

        var span = acquireSpans[0];
        span.GetTagItem(TelemetryConstants.TagNames.LockAcquired).ShouldBe(false);

        // Contention/exhaustion is an expected outcome, not a failure.
        span.Status.ShouldBe(ActivityStatusCode.Unset);

        // No handle was ever acquired, so disposal must not emit a Lock.Release.
        collected.Any(a => IsSpan(a, "Lock.Release", key)).ShouldBeFalse();
    }

    [Fact]
    public async Task AcquireThenDispose_EmitsOneAcquireSpanAndOneReleaseSpan_PairedOnLockKey()
    {
        // The symmetry the controller asked for: a successful acquisition on this funnel now
        // produces exactly one Lock.Acquire and, on disposal, exactly one Lock.Release — no orphan.
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Pipeline", collected);

        const string key = "vnext:bank:parent-flow:acquire-release-pair";
        var handle = Substitute.For<IDistributedLockHandle>();
        var lockService = Substitute.For<IDistributedLockService>();
        lockService.TryAcquireLockAsync(key, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(handle);

        var factory = CreateFactory(lockService);

        var scope = await factory.AcquireAsync(key);
        await scope.DisposeAsync();

        collected.Count(a => IsSpan(a, "Lock.Acquire", key)).ShouldBe(1);
        var releaseSpan = Assert.Single(collected, a => IsSpan(a, "Lock.Release", key));
        releaseSpan.GetTagItem(TelemetryConstants.TagNames.LockKind).ShouldBe("chain");
    }
}
