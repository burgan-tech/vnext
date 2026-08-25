using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedLock;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Infrastructure.Execution.Locks;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Locks;

/// <summary>
/// Pins the <c>Lock.Acquire</c> / <c>Lock.Release</c> spans emitted around the status-lock
/// funnel (<see cref="InstanceStatusLock.AcquireAsync"/> and <see cref="TransitionLockScope"/>
/// disposal). Contention (acquire returns no handle) is an expected outcome, not an error status —
/// it must surface only via <c>vnext.lock.acquired = false</c>, never an error-status span.
/// </summary>
public sealed class InstanceStatusLockActivityTests : IDisposable
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

    private static InstanceStatusLock CreateSut(IDistributedLockService lockService) => new(
        lockService,
        Options.Create(new WorkflowExecutionOptions { StatusLockLeaseSeconds = 5 }),
        NullLogger<InstanceStatusLock>.Instance);

    [Fact]
    public async Task AcquireAsync_WhenContended_EmitsLockAcquireSpan_NotAcquired_WithoutErrorStatus()
    {
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Pipeline", collected);

        var lockService = Substitute.For<IDistributedLockService>();
        lockService.TryAcquireLockAsync("k1", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IDistributedLockHandle?)null); // contention: not acquired

        var sut = CreateSut(lockService);

        await using var scope = await sut.AcquireAsync("k1");

        scope.IsAcquired.ShouldBeFalse();

        var span = Assert.Single(collected, a => a.DisplayName == "Lock.Acquire");
        span.GetTagItem(TelemetryConstants.TagNames.LockKey).ShouldBe("k1");
        span.GetTagItem(TelemetryConstants.TagNames.LockAcquired).ShouldBe(false);
        span.GetTagItem(TelemetryConstants.TagNames.LockLeaseSeconds).ShouldBe(5);

        // Contention is an expected outcome, not a failure — the span must not carry error status.
        span.Status.ShouldBe(ActivityStatusCode.Unset);

        // Disposing a not-acquired scope has no handle to release — no Lock.Release span.
        collected.Any(a => a.DisplayName == "Lock.Release").ShouldBeFalse();
    }

    [Fact]
    public async Task AcquireAsync_WhenAcquired_EmitsLockAcquireSpan_Acquired_AndReleaseSpanOnDispose()
    {
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Pipeline", collected);

        var handle = Substitute.For<IDistributedLockHandle>();
        var lockService = Substitute.For<IDistributedLockService>();
        lockService.TryAcquireLockAsync("k2", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(handle);

        var sut = CreateSut(lockService);

        var scope = await sut.AcquireAsync("k2");
        scope.IsAcquired.ShouldBeTrue();

        var acquireSpan = Assert.Single(collected, a => a.DisplayName == "Lock.Acquire");
        acquireSpan.GetTagItem(TelemetryConstants.TagNames.LockKey).ShouldBe("k2");
        acquireSpan.GetTagItem(TelemetryConstants.TagNames.LockAcquired).ShouldBe(true);

        // Release span only appears once the scope is disposed.
        collected.Any(a => a.DisplayName == "Lock.Release").ShouldBeFalse();

        await scope.DisposeAsync();

        var releaseSpan = Assert.Single(collected, a => a.DisplayName == "Lock.Release");
        releaseSpan.GetTagItem(TelemetryConstants.TagNames.LockKey).ShouldBe("k2");
        await handle.Received(1).DisposeAsync();
    }
}
