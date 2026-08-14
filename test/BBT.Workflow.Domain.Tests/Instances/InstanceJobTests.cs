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
}
