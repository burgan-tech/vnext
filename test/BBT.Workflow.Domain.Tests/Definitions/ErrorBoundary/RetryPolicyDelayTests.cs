using System;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions.ErrorBoundary;

/// <summary>
/// Pins the retry ladder arithmetic and, deliberately, that SUB-SECOND initial delays survive the
/// ISO-8601 round trip.
/// </summary>
/// <remarks>
/// A lock-contention 409 in this engine means "the short status check-and-set lock was held by
/// someone else" — it clears in milliseconds. Backing off a full second for it is two orders of
/// magnitude too slow, and the only reason a domain would not author <c>PT0.05S</c> is doubt about
/// whether the runtime accepts it. <see cref="XmlConvert.ToTimeSpan"/> does, and the schema's
/// duration pattern allows fractional seconds; this test is what makes that safe to rely on.
/// </remarks>
public class RetryPolicyDelayTests
{
    [Theory]
    [InlineData("PT0.05S", 50)]
    [InlineData("PT0.1S", 100)]
    [InlineData("PT0.5S", 500)]
    [InlineData("PT1S", 1000)]
    [InlineData("PT2S", 2000)]
    public void InitialDelayIso8601_RoundTripsSubSecondDurations(string iso, int expectedMs)
    {
        var policy = new RetryPolicy { InitialDelayIso8601 = iso };

        policy.InitialDelay.TotalMilliseconds.ShouldBe(expectedMs);
        // The getter must produce a string the schema pattern still accepts.
        new RetryPolicy { InitialDelayIso8601 = policy.InitialDelayIso8601 }
            .InitialDelay.ShouldBe(policy.InitialDelay);
    }

    [Fact]
    public void ExponentialLadder_FromFiftyMilliseconds_StaysUnderMaxDelay()
    {
        // The shape a lock-contention rule wants: retry almost immediately, a handful of times,
        // capped well under a second so five exhausted attempts still cost ~1s, not ~25s.
        var policy = new RetryPolicy
        {
            MaxRetries = 5,
            InitialDelayIso8601 = "PT0.05S",
            BackoffType = BackoffType.Exponential,
            BackoffMultiplier = 2,
            MaxDelayIso8601 = "PT0.4S"
        };

        policy.CalculateDelay(1).TotalMilliseconds.ShouldBe(50);
        policy.CalculateDelay(2).TotalMilliseconds.ShouldBe(100);
        policy.CalculateDelay(3).TotalMilliseconds.ShouldBe(200);
        policy.CalculateDelay(4).TotalMilliseconds.ShouldBe(400);
        policy.CalculateDelay(5).TotalMilliseconds.ShouldBe(400, "capped at MaxDelay");

        var worstCase = 0d;
        for (var attempt = 1; attempt <= policy.MaxRetries; attempt++)
            worstCase += policy.CalculateDelay(attempt).TotalMilliseconds;
        worstCase.ShouldBeLessThan(1500);
    }

    [Fact]
    public void ExponentialLadder_FromOneSecond_IsTheExpensiveShapeItReplaces()
    {
        // The policy this change moves 409 OFF of: five exhausted attempts cost ~25 seconds.
        var policy = new RetryPolicy
        {
            MaxRetries = 5,
            InitialDelayIso8601 = "PT1S",
            BackoffType = BackoffType.Exponential,
            BackoffMultiplier = 2,
            MaxDelayIso8601 = "PT10S"
        };

        var worstCase = 0d;
        for (var attempt = 1; attempt <= policy.MaxRetries; attempt++)
            worstCase += policy.CalculateDelay(attempt).TotalMilliseconds;

        worstCase.ShouldBe(25_000);
    }
}
