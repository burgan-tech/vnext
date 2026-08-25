using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Instances;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Instances;

/// <summary>
/// Pins the contract of the shared per-instance write gate. Two independent writers of the same
/// instance — <c>InstanceDataWriteService</c>'s data append and
/// <c>SubProcessTaskExecutor.CreateCorrelationAsync</c>'s correlation write — must land on the
/// SAME semaphore, or they serialize against nobody and collide on the shared DbContext.
/// </summary>
public sealed class InstanceWriteGateTests
{
    /// <summary>
    /// The sharing invariant, asserted at the primitive: whoever asks, the same id yields the same
    /// gate object. Two per-writer arrays would break this and reintroduce the fan-out collision.
    /// </summary>
    [Fact]
    public void GateFor_SameInstanceId_AlwaysResolvesTheSameSemaphore()
    {
        var instanceId = Guid.NewGuid();

        var fromDataWritePath = InstanceWriteGate.GateFor(instanceId);
        var fromCorrelationPath = InstanceWriteGate.GateFor(instanceId);

        fromCorrelationPath.ShouldBeSameAs(fromDataWritePath);
    }

    [Fact]
    public void StripeIndexOf_IsStableAndBounded()
    {
        for (var i = 0; i < 500; i++)
        {
            var id = Guid.NewGuid();
            var index = InstanceWriteGate.StripeIndexOf(id);

            index.ShouldBeGreaterThanOrEqualTo(0);
            index.ShouldBeLessThan(256);
            InstanceWriteGate.StripeIndexOf(id).ShouldBe(index);
        }
    }

    [Fact]
    public async Task AcquireAsync_SameInstanceId_SerializesHolders()
    {
        var instanceId = Guid.NewGuid();
        var active = 0;
        var maxConcurrent = 0;

        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using (await InstanceWriteGate.AcquireAsync(instanceId))
            {
                var current = Interlocked.Increment(ref active);
                InterlockedMax(ref maxConcurrent, current);
                await Task.Delay(30);
                Interlocked.Decrement(ref active);
            }
        }));

        maxConcurrent.ShouldBe(1);
    }

    /// <summary>
    /// Striping must not degenerate into a global lock — only a hash collision may serialize two
    /// unrelated instances, so two ids known to be on different stripes run concurrently.
    /// </summary>
    [Fact]
    public async Task AcquireAsync_DifferentStripes_DoNotSerialize()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        while (InstanceWriteGate.StripeIndexOf(second) == InstanceWriteGate.StripeIndexOf(first))
        {
            second = Guid.NewGuid();
        }

        var firstInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Each holder waits for the other while holding its own gate. They can only both finish if
        // the gates are genuinely independent; one global lock would deadlock.
        var both = Task.WhenAll(
            HoldAsync(first, firstInside, secondInside),
            HoldAsync(second, secondInside, firstInside));

        var raced = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(5)));
        raced.ShouldBe((Task)both);
        return;

        static async Task HoldAsync(Guid id, TaskCompletionSource announce, TaskCompletionSource other)
        {
            using (await InstanceWriteGate.AcquireAsync(id))
            {
                announce.TrySetResult();
                await other.Task;
            }
        }
    }

    [Fact]
    public async Task AcquireAsync_ReleasesOnDispose_SoTheNextHolderProceeds()
    {
        var instanceId = Guid.NewGuid();
        Task<InstanceWriteGate.Releaser> blocked;

        using (await InstanceWriteGate.AcquireAsync(instanceId))
        {
            blocked = InstanceWriteGate.AcquireAsync(instanceId);
            var raced = await Task.WhenAny(blocked, Task.Delay(200));
            raced.ShouldNotBe((Task)blocked, "the gate must still be held");
        }

        var released = await Task.WhenAny(blocked, Task.Delay(TimeSpan.FromSeconds(5)));
        released.ShouldBe((Task)blocked, "disposing the handle must let the next holder in");
        (await blocked).Dispose();
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var observed = Volatile.Read(ref target);
        while (value > observed)
        {
            var previous = Interlocked.CompareExchange(ref target, value, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }
}
