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
