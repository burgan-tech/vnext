using System;
using System.Text.RegularExpressions;
using BBT.Workflow.Execution.LongPoll;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for the structured <see cref="JobName"/> value object: builder/parser round-trips,
/// source-state scoping, the Dapr-safe character alphabet, key validation, and legacy single-field
/// read-compatibility.
/// </summary>
public class JobNameTests
{
    private static readonly Guid Instance = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
    private const string InstanceN = "550e8400e29b41d4a716446655440000";

    // Dapr's Jobs API only accepts names in this alphabet (the name becomes the /job/{name} route).
    private static readonly Regex DaprSafe = new("^[A-Za-z0-9_.-]+$", RegexOptions.Compiled);

    [Fact]
    public void ForAsyncTransition_ShouldProduceReadableName_AndRoundTrip()
    {
        var jobName = JobName.ForAsyncTransition(Instance, "wait-ivr", "go-to-transfer-ivr");

        // Readable, dot-delimited, no marker/encoding characters.
        Assert.Equal($"vnext.job.v1.tx.{InstanceN}.wait-ivr.go-to-transfer-ivr", jobName.Value);

        Assert.True(JobName.TryParse(jobName.Value, out var parsed));
        Assert.Equal(JobType.AsyncTransition, parsed.Type);
        Assert.Equal(Instance, parsed.InstanceId);
        Assert.Equal("wait-ivr", parsed.SourceState);
        Assert.Equal("go-to-transfer-ivr", parsed.TransitionKey);
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
    public void EmptySourceState_ShouldDegradeToSingleKeyName()
    {
        var jobName = JobName.ForAsyncTransition(Instance, "", "approve");

        Assert.Equal($"vnext.job.v1.tx.{InstanceN}.approve", jobName.Value);
        Assert.Null(JobName.Parse(jobName.Value).SourceState);
        Assert.Equal("approve", JobName.Parse(jobName.Value).TransitionKey);
    }

    [Fact]
    public void ForTimeout_ShouldHaveNoKey_AndNullSourceState()
    {
        var jobName = JobName.ForTimeout(Instance);

        Assert.Equal($"vnext.job.v1.to.{InstanceN}", jobName.Value);

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
    public void LegacySingleFieldName_ShouldParse_AsNullSourceState()
    {
        // A pre-rollout tx name (no source-state field) must still parse cleanly.
        var parsed = JobName.Parse($"vnext.job.v1.tx.{InstanceN}.approve");

        Assert.Null(parsed.SourceState);
        Assert.Equal("approve", parsed.TransitionKey);
        Assert.Equal("approve", parsed.Segment);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("go-back")]      // hyphen — the original suffix-match bug case
    [InlineData("go-to-transfer-ivr")]
    [InlineData("step_1")]       // underscore
    [InlineData("State-A_1")]
    public void SafeKeys_ShouldRoundTripPlain(string key)
    {
        var jobName = JobName.ForAsyncTransition(Instance, key, key);

        Assert.Matches(DaprSafe, jobName.Value);
        Assert.DoesNotContain("~", jobName.Value);

        Assert.True(JobName.TryParse(jobName.Value, out var parsed));
        Assert.Equal(key, parsed.SourceState);
        Assert.Equal(key, parsed.TransitionKey);
        Assert.Equal(JobType.AsyncTransition, parsed.Type);
        Assert.Equal(Instance, parsed.InstanceId);
    }

    [Theory]
    [InlineData("a.b.c")]        // dots collide with the delimiter
    [InlineData("with space")]
    [InlineData("son-aşama")]    // non-ascii
    [InlineData("emoji-🚀-key")]
    [InlineData("with:colon")]
    public void NonDaprSafeKey_ShouldThrow(string key)
    {
        Assert.Throws<ArgumentException>(() => JobName.ForAsyncTransition(Instance, "state-a", key));
        Assert.Throws<ArgumentException>(() => JobName.ForAsyncTransition(Instance, key, "approve"));
    }

    [Fact]
    public void EmptyTransitionKey_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => JobName.ForAsyncTransition(Instance, "state-a", ""));
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
    [InlineData("")]
    [InlineData(null)]
    [InlineData("vnext.job.v1.zz.550e8400e29b41d4a716446655440000")]        // unknown type code
    [InlineData("vnext.job.v1.tx.not-a-guid.approve")]                      // bad instance id
    [InlineData("vnext.job.v1.to.550e8400e29b41d4a716446655440000.extra")]  // timeout must have no key
    [InlineData("garbage")]
    public void TryParse_ShouldRejectForeignAndInvalidNames(string? value)
    {
        Assert.False(JobName.TryParse(value, out _));
    }

    [Fact]
    public void Parse_ShouldThrow_OnInvalidName()
    {
        Assert.Throws<FormatException>(() => JobName.Parse("trans-abc-go"));
    }
}
