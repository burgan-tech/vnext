using System;
using Xunit;

namespace BBT.Workflow.Instances;

public class InstanceJobTests
{
    [Fact]
    public void Create_WithExecuteAt_StoresTheUtcInstant()
    {
        // A zoned offset must land as its UTC instant with Kind.Utc — the state function
        // serializes ExecuteAt as executeAtUtc and relies on the kind for the Z designator.
        var instanceId = Guid.NewGuid();
        var executeAt = new DateTimeOffset(2026, 8, 3, 17, 30, 0, TimeSpan.FromHours(3));

        var job = InstanceJob.Create(
            Guid.NewGuid(),
            JobName.ForScheduledTransition(instanceId, "review", "payment-timeout", Guid.NewGuid()),
            Guid.NewGuid(), "test-domain", "test-flow", instanceId, executeAt);

        Assert.Equal(new DateTime(2026, 8, 3, 14, 30, 0, DateTimeKind.Utc), job.ExecuteAt);
        Assert.Equal(DateTimeKind.Utc, job.ExecuteAt!.Value.Kind);
    }

    [Fact]
    public void Create_WithoutExecuteAt_LeavesItNull()
    {
        var instanceId = Guid.NewGuid();

        var job = InstanceJob.Create(
            Guid.NewGuid(),
            JobName.ForTimeout(instanceId),
            Guid.NewGuid(), "test-domain", "test-flow", instanceId);

        Assert.Null(job.ExecuteAt);
    }

    private static readonly DateTime Now = new(2026, 10, 20, 7, 29, 9, DateTimeKind.Utc);

    private static InstanceJob ScheduledJob(string transitionKey, DateTimeOffset? executeAt)
    {
        var instanceId = Guid.NewGuid();
        return InstanceJob.Create(
            Guid.NewGuid(),
            JobName.ForScheduledTransition(instanceId, "contract-initial-wait", transitionKey, Guid.NewGuid()),
            Guid.NewGuid(), "test-domain", "test-flow", instanceId, executeAt);
    }

    [Fact]
    public void IsLiveTransitionOwner_ArmedTimerNotYetDue_IsNotAnOwner()
    {
        // The QA stall: a Busy-subtype wait state arms its 2-hour timeout; an updateData two
        // seconds later must not read that waiting timer as the Busy's owner.
        var job = ScheduledJob("intl-contract-timeout", new DateTimeOffset(Now.AddHours(2)));

        Assert.False(job.IsLiveTransitionOwner("international-account-process-update-data", Now));
    }

    [Fact]
    public void IsLiveTransitionOwner_DueTimer_IsAnOwner()
    {
        // Reached its time: it is firing (or about to) and takes Busy through its own admission.
        var job = ScheduledJob("intl-contract-timeout", new DateTimeOffset(Now.AddSeconds(-1)));

        Assert.True(job.IsLiveTransitionOwner("international-account-process-update-data", Now));
    }

    [Fact]
    public void IsLiveTransitionOwner_TimerWithoutExecuteAt_StaysAnOwner()
    {
        // A row from before the ExecuteAt column cannot be dated; it keeps the old meaning.
        var job = ScheduledJob("intl-contract-timeout", executeAt: null);

        Assert.True(job.IsLiveTransitionOwner("international-account-process-update-data", Now));
    }

    [Fact]
    public void IsLiveTransitionOwner_AsyncTransitionForAnotherKey_IsAnOwner()
    {
        var instanceId = Guid.NewGuid();
        var job = InstanceJob.Create(
            Guid.NewGuid(),
            JobName.ForAsyncTransition(instanceId, "review", "approve", Guid.NewGuid()),
            Guid.NewGuid(), "test-domain", "test-flow", instanceId);

        Assert.True(job.IsLiveTransitionOwner("update-data", Now));
    }

    [Fact]
    public void IsLiveTransitionOwner_JobForTheRequestingKey_IsNotAnOwner()
    {
        // The execution's own accept intent (key compared case-insensitively).
        var instanceId = Guid.NewGuid();
        var job = InstanceJob.Create(
            Guid.NewGuid(),
            JobName.ForAsyncTransition(instanceId, "review", "update-data", Guid.NewGuid()),
            Guid.NewGuid(), "test-domain", "test-flow", instanceId);

        Assert.False(job.IsLiveTransitionOwner("Update-Data", Now));
    }

    [Fact]
    public void IsLiveTransitionOwner_NonTransitionJob_IsNotAnOwner()
    {
        var instanceId = Guid.NewGuid();
        var job = InstanceJob.Create(
            Guid.NewGuid(), JobName.ForTimeout(instanceId),
            Guid.NewGuid(), "test-domain", "test-flow", instanceId, new DateTimeOffset(Now.AddSeconds(-1)));

        Assert.False(job.IsLiveTransitionOwner("update-data", Now));
    }
}
