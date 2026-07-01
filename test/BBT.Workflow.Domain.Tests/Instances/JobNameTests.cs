using System;
using BBT.Workflow.Execution.LongPoll;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for the structured <see cref="JobName"/> value object: builder/parser round-trips,
/// segment encoding collision cases, source-state scoping, job-type distinctness, and legacy-name rejection.
/// </summary>
public class JobNameTests
{
    private static readonly Guid Instance = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");

    [Fact]
    public void ForAsyncTransition_ShouldRoundTrip()
    {
        var jobName = JobName.ForAsyncTransition(Instance, "state-a", "approve");

        Assert.True(JobName.TryParse(jobName.Value, out var parsed));
        Assert.Equal(JobType.AsyncTransition, parsed.Type);
        Assert.Equal(Instance, parsed.InstanceId);
        Assert.Equal("state-a", parsed.SourceState);
        Assert.Equal("approve", parsed.TransitionKey);
    }

    [Fact]
    public void ForScheduledTransition_ShouldUseDistinctTypeCode()
    {
        var jobName = JobName.ForScheduledTransition(Instance, "state-a", "approve");

        Assert.StartsWith("vnext.job.v1.sx.", jobName.Value);
        Assert.Equal(JobType.ScheduledTransition, JobName.Parse(jobName.Value).Type);
    }

    [Fact]
    public void Async_And_Scheduled_ForSameInstanceAndKey_ShouldDiffer()
    {
        var async = JobName.ForAsyncTransition(Instance, "state-a", "approve");
        var scheduled = JobName.ForScheduledTransition(Instance, "state-a", "approve");

        Assert.NotEqual(async.Value, scheduled.Value);
        Assert.NotEqual(JobName.Parse(async.Value).Type, JobName.Parse(scheduled.Value).Type);
    }

    [Fact]
    public void SameTransitionKey_DifferentSourceState_ShouldProduceDifferentNames()
    {
        // The bug fix: two states each defining a "within" transition must NOT collide into one job.
        var fromA = JobName.ForAsyncTransition(Instance, "state-a", "within");
        var fromB = JobName.ForAsyncTransition(Instance, "state-b", "within");

        Assert.NotEqual(fromA.Value, fromB.Value);
        Assert.Equal("state-a", JobName.Parse(fromA.Value).SourceState);
        Assert.Equal("state-b", JobName.Parse(fromB.Value).SourceState);
        Assert.Equal("within", JobName.Parse(fromA.Value).TransitionKey);
        Assert.Equal("within", JobName.Parse(fromB.Value).TransitionKey);
    }

    [Fact]
    public void SameSourceState_SameKey_ShouldBeIdentical()
    {
        // Idempotency: a retry / outbox re-publish of the same logical hop must dedup to one name.
        var first = JobName.ForAsyncTransition(Instance, "state-a", "within");
        var second = JobName.ForAsyncTransition(Instance, "state-a", "within");

        Assert.Equal(first.Value, second.Value);
    }

    [Fact]
    public void ForTimeout_ShouldHaveNoSegment_AndNullSourceState()
    {
        var jobName = JobName.ForTimeout(Instance);

        Assert.Equal("vnext.job.v1.to.550e8400e29b41d4a716446655440000", jobName.Value);

        Assert.True(JobName.TryParse(jobName.Value, out var parsed));
        Assert.Equal(JobType.Timeout, parsed.Type);
        Assert.Null(parsed.Segment);
        Assert.Null(parsed.SourceState);
        Assert.Null(parsed.TransitionKey);
    }

    [Fact]
    public void ForLongPollAck_ShouldCarryWellKnownKey_AndNullSourceState()
    {
        var jobName = JobName.ForLongPollAck(Instance);

        Assert.True(JobName.TryParse(jobName.Value, out var parsed));
        Assert.Equal(JobType.LongPollAck, parsed.Type);
        Assert.Equal(LongPollAckConstants.JobKey, parsed.TransitionKey);
        Assert.Null(parsed.SourceState);
    }

    [Fact]
    public void LegacyV1PlainSegment_ShouldParse_AsNullSourceState()
    {
        // A pre-rollout tx name (no source-state composite) must still parse cleanly.
        var parsed = JobName.Parse("vnext.job.v1.tx.550e8400e29b41d4a716446655440000.approve");

        Assert.Null(parsed.SourceState);
        Assert.Equal("approve", parsed.TransitionKey);
        Assert.Equal("approve", parsed.Segment);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("go-back")]               // hyphen — the original suffix-match bug case
    [InlineData("step_1")]
    [InlineData("a.b.c")]                 // dots — would collide with the delimiter if not encoded
    [InlineData("with:colon")]
    [InlineData("with space")]
    [InlineData("son-aşama")]             // non-ascii
    [InlineData("emoji-🚀-key")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    public void SourceStateAndKey_ShouldRoundTripForArbitraryKeys(string key)
    {
        var jobName = JobName.ForAsyncTransition(Instance, key, key);

        Assert.True(JobName.TryParse(jobName.Value, out var parsed));
        Assert.Equal(key, parsed.SourceState);
        Assert.Equal(key, parsed.TransitionKey);
        Assert.Equal(JobType.AsyncTransition, parsed.Type);
        Assert.Equal(Instance, parsed.InstanceId);
    }

    [Fact]
    public void Build_ShouldStayWithinMaxLength_ForMaxSourceStateAndKey()
    {
        var key = new string('x', 100);  // WorkflowConstants.MaxKeyLength
        var jobName = JobName.ForScheduledTransition(Instance, key, key);

        Assert.True(jobName.Value.Length <= InstanceJobConstants.MaxJobNameLength);
    }

    [Theory]
    [InlineData("trans-550e8400-e29b-41d4-a716-446655440000-approve")]
    [InlineData("timeout-550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("lpack-550e8400-e29b-41d4-a716-446655440000-longpoll-ack")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("vnext.job.v1.zz.550e8400e29b41d4a716446655440000")]   // unknown type code
    [InlineData("vnext.job.v1.tx.not-a-guid.approve")]                 // bad instance id
    [InlineData("garbage")]
    public void TryParse_ShouldRejectLegacyAndInvalidNames(string? value)
    {
        Assert.False(JobName.TryParse(value, out _));
    }

    [Fact]
    public void Parse_ShouldThrow_OnInvalidName()
    {
        Assert.Throws<FormatException>(() => JobName.Parse("trans-abc-go"));
    }
}
