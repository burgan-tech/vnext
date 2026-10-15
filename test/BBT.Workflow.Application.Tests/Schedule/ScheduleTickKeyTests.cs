using System;
using BBT.Workflow.Instances;
using BBT.Workflow.Schedule;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Schedule;

/// <summary>
/// Pins the key derivation that makes a scheduled start safe on more than one replica.
/// <para>
/// The invariant under test is narrow but load-bearing: every replica firing the same tick must
/// compute the same key, and ticks that are genuinely different must not collide.
/// </para>
/// </summary>
public sealed class ScheduleTickKeyTests
{
    private const string Tick = "2026-10-05 07:25:42.5 +0000 UTC";

    private static readonly DateTime AnyClock = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The reason the key is truncated to the second. Dapr's cron binding stamps
    /// <c>readTimeUTC</c> from each sidecar's own clock as the tick fires, so replicas disagree in the
    /// sub-second digits. If those digits reached the key, an N-replica deployment would create N
    /// instances per tick instead of one.
    /// </summary>
    [Fact]
    public void Replicas_disagreeing_on_sub_second_digits_still_derive_the_same_key()
    {
        var replicaA = ScheduleTickKey.Derive(
            "rezervation", "daily", "2026-10-05 07:25:42.93172757 +0000 UTC", AnyClock);
        var replicaB = ScheduleTickKey.Derive(
            "rezervation", "daily", "2026-10-05 07:25:42.118394 +0000 UTC", AnyClock);
        var replicaC = ScheduleTickKey.Derive(
            "rezervation", "daily", "2026-10-05 07:25:42 +0000 UTC", AnyClock);

        replicaA.ShouldBe(replicaB);
        replicaB.ShouldBe(replicaC);
        replicaA.ShouldBe("daily-20261005T072542Z");
    }

    /// <summary>Consecutive ticks must stay distinct, or a schedule would only ever run once.</summary>
    [Fact]
    public void Different_ticks_derive_different_keys()
    {
        var first = ScheduleTickKey.Derive(
            "rezervation", "daily", "2026-10-05 07:25:42.1 +0000 UTC", AnyClock);
        var second = ScheduleTickKey.Derive(
            "rezervation", "daily", "2026-10-05 07:25:43.1 +0000 UTC", AnyClock);

        first.ShouldNotBe(second);
    }

    /// <summary>
    /// Two components may target the same workflow on overlapping schedules. When their ticks
    /// coincide, the scheduleId is what keeps them from collapsing into a single instance.
    /// </summary>
    [Fact]
    public void Coincident_ticks_of_different_schedules_stay_distinct()
    {
        const string tick = "2026-10-05 07:25:42.5 +0000 UTC";

        var hourly = ScheduleTickKey.Derive("rezervation", "hourly", tick, AnyClock);
        var daily = ScheduleTickKey.Derive("rezervation", "daily", tick, AnyClock);

        hourly.ShouldNotBe(daily);
    }

    /// <summary>
    /// Without a scheduleId the workflow key scopes the tick. That deliberately collapses coincident
    /// ticks of the same workflow — documented behaviour, and the reason scheduleId exists.
    /// </summary>
    [Fact]
    public void Missing_scheduleId_falls_back_to_the_workflow_key()
    {
        const string tick = "2026-10-05 07:25:42.5 +0000 UTC";

        ScheduleTickKey.Derive("rezervation", null, tick, AnyClock)
            .ShouldBe("rezervation-20261005T072542Z");
        ScheduleTickKey.Derive("rezervation", "   ", tick, AnyClock)
            .ShouldBe("rezervation-20261005T072542Z");
    }

    /// <summary>
    /// A caller that is not a Dapr cron binding sends no tick header. The start must still work; the
    /// key falls back to this runtime's clock, which only collapses calls landing in the same second.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-timestamp")]
    [InlineData("2026-10-05")] // shorter than second precision
    public void Unusable_tick_header_falls_back_to_the_clock(string? header)
    {
        var clock = new DateTime(2026, 10, 5, 9, 30, 15, DateTimeKind.Utc);

        ScheduleTickKey.Derive("rezervation", "daily", header, clock)
            .ShouldBe("daily-20261005T093015Z");
        ScheduleTickKey.TryParseTick(header, out _).ShouldBeFalse();
    }

    /// <summary>
    /// Go's <c>time.Time.String()</c> trims trailing zeros from the fractional part, so the digit
    /// count varies between ticks. Parsing must not depend on it.
    /// </summary>
    [Theory]
    [InlineData("2026-10-05 07:25:42.93172757 +0000 UTC")]
    [InlineData("2026-10-05 07:25:42.9 +0000 UTC")]
    [InlineData("2026-10-05 07:25:42 +0000 UTC")]
    public void Variable_fractional_precision_is_parsed(string header)
    {
        ScheduleTickKey.TryParseTick(header, out var tick).ShouldBeTrue();

        tick.ShouldBe(new DateTime(2026, 10, 5, 7, 25, 42, DateTimeKind.Utc));
        tick.Kind.ShouldBe(DateTimeKind.Utc);
    }

    /// <summary>
    /// <c>Instances.Key</c> is capped at <see cref="InstanceConstants.MaxKeyLength"/>. A workflow key
    /// may itself be that long, so appending the instant overflows the column before anyone supplies
    /// an over-long scheduleId — and since nothing retries a cron tick, the schedule would just stop
    /// producing instances.
    /// </summary>
    [Theory]
    [InlineData(50)]
    [InlineData(83)]
    [InlineData(84)]
    [InlineData(100)]
    [InlineData(400)]
    public void Key_never_exceeds_the_instance_key_column(int scopeLength)
    {
        var scope = new string('w', scopeLength);

        var fromWorkflow = ScheduleTickKey.Derive(scope, null, Tick, AnyClock);
        var fromScheduleId = ScheduleTickKey.Derive("flow", scope, Tick, AnyClock);

        fromWorkflow.Length.ShouldBeLessThanOrEqualTo(InstanceConstants.MaxKeyLength);
        fromScheduleId.Length.ShouldBeLessThanOrEqualTo(InstanceConstants.MaxKeyLength);
    }

    /// <summary>
    /// Shortening must stay deterministic ACROSS PROCESSES, or replicas would derive different keys
    /// and the deduplication this type exists for would silently stop working.
    /// <para>
    /// Pinned against a literal computed outside this process, because that is the only form of the
    /// assertion that can fail: calling <c>Derive</c> twice and comparing would also pass with
    /// <see cref="string.GetHashCode()"/>, which .NET randomises per process and which is exactly the
    /// regression this guards. The literal pins the digest algorithm, its length and the separator at
    /// the same time.
    /// </para>
    /// </summary>
    [Fact]
    public void Shortened_scopes_are_derived_deterministically_across_processes()
    {
        var key = ScheduleTickKey.Derive("flow", new string('x', 300), Tick, AnyClock);

        key.ShouldBe(
            "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
            + "-0d4e2ca9-20261005T072542Z");
        key.Length.ShouldBe(InstanceConstants.MaxKeyLength);
    }

    /// <summary>
    /// Two long scopes sharing a prefix must not collapse into one key — truncation alone would do
    /// exactly that, which is why the digest covers the original value.
    /// </summary>
    [Fact]
    public void Long_scopes_sharing_a_prefix_stay_distinct()
    {
        var prefix = new string('y', 120);

        var first = ScheduleTickKey.Derive("flow", prefix + "-alpha", Tick, AnyClock);
        var second = ScheduleTickKey.Derive("flow", prefix + "-beta", Tick, AnyClock);

        first.ShouldNotBe(second);
    }
}
