using System;
using BBT.Workflow.Definitions.Timer;
using Xunit;

namespace BBT.Workflow.Definitions;

public class TimerScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ResolveExecuteAt_DateTimeSchedule_ReturnsTheScheduledInstant()
    {
        var scheduledAt = new DateTime(2026, 8, 3, 14, 30, 0, DateTimeKind.Utc);
        var schedule = TimerSchedule.FromDateTime(scheduledAt);

        var executeAt = schedule.ResolveExecuteAt(Now);

        Assert.Equal(new DateTimeOffset(scheduledAt), executeAt);
    }

    /// <summary>
    /// An Unspecified-kind DateTime (the kind DateTime.Parse yields for ISO strings without an
    /// offset) is interpreted as UTC, never through the host time zone — the firing instant must
    /// not depend on where the runtime runs. Behavior-preserving on UTC deployment hosts.
    /// </summary>
    [Fact]
    public void ResolveExecuteAt_UnspecifiedKindDateTime_IsInterpretedAsUtc()
    {
        var unspecified = DateTime.Parse("2026-08-03T14:30:00",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(DateTimeKind.Unspecified, unspecified.Kind);

        var executeAt = TimerSchedule.FromDateTime(unspecified).ResolveExecuteAt(Now);

        Assert.Equal(new DateTimeOffset(2026, 8, 3, 14, 30, 0, TimeSpan.Zero), executeAt);
    }

    /// <summary>
    /// A Local-kind DateTime is not ambiguous — it denotes exactly one instant (the host's wall
    /// clock) — so it is honored rather than rejected, matching the pre-existing FromDateTime
    /// contract.
    /// </summary>
    [Fact]
    public void ResolveExecuteAt_LocalKindDateTime_ResolvesToItsOwnInstant()
    {
        var local = new DateTime(2026, 8, 3, 14, 30, 0, DateTimeKind.Local);

        var executeAt = TimerSchedule.FromDateTime(local).ResolveExecuteAt(Now);

        Assert.Equal(local.ToUniversalTime(), executeAt.UtcDateTime);
    }

    [Fact]
    public void ResolveExecuteAt_DurationSchedule_ReturnsNowPlusDuration()
    {
        var schedule = TimerSchedule.FromDuration(TimeSpan.FromMinutes(90));

        var executeAt = schedule.ResolveExecuteAt(Now);

        Assert.Equal(Now.AddMinutes(90), executeAt);
    }

    [Fact]
    public void ResolveExecuteAt_ImmediateSchedule_ReturnsNowPlusTwoSeconds()
    {
        // The two-second offset mirrors what the Dapr arming has always used for Immediate,
        // so the persisted execution time matches the armed trigger.
        var schedule = TimerSchedule.Immediate();

        var executeAt = schedule.ResolveExecuteAt(Now);

        Assert.Equal(Now.AddSeconds(2), executeAt);
    }

    /// <summary>
    /// Duration and Immediate schedules resolve relative to the supplied clock, never to a clock
    /// read of their own — that is what lets a caller arm the scheduler and persist the execution
    /// time from one instant without drift.
    /// </summary>
    [Fact]
    public void ResolveExecuteAt_IsDeterministicForTheSameClock()
    {
        var schedule = TimerSchedule.FromDuration(TimeSpan.FromHours(1));

        Assert.Equal(schedule.ResolveExecuteAt(Now), schedule.ResolveExecuteAt(Now));
        Assert.Equal(Now.AddHours(2), schedule.ResolveExecuteAt(Now.AddHours(1)));
    }
}
