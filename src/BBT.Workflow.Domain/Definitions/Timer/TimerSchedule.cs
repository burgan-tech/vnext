using System.Text.Json.Serialization;
using BBT.Aether;
using BBT.Aether.Domain.Values;
using Dapr.Jobs;
using Dapr.Jobs.Models;
using Dapr.Jobs.Extensions;

namespace BBT.Workflow.Definitions.Timer;

/// <summary>
/// Represents a flexible timer schedule that can be used for workflow timing operations.
/// This class provides compatibility with Dapr job scheduling while maintaining workflow-specific functionality.
/// </summary>
public class TimerSchedule : ValueObject
{
    private TimerSchedule()
    {
    }

    [JsonConstructor]
    private TimerSchedule(
        TimerScheduleType scheduleType,
        string? expression,
        DateTime? scheduledDateTime,
        TimeSpan? duration)
    {
        ScheduleType = scheduleType;
        Expression = expression;
        ScheduledDateTime = scheduledDateTime;
        Duration = duration;
        
        ValidateSchedule();
    }

    /// <summary>
    /// The type of timer schedule (DateTime, Cron, Duration, or Immediate).
    /// </summary>
    public TimerScheduleType ScheduleType { get; private set; }

    /// <summary>
    /// The expression value for the schedule. For Cron schedules, this contains the cron expression.
    /// For other types, this may contain additional configuration data.
    /// </summary>
    public string? Expression { get; private set; }

    /// <summary>
    /// The specific DateTime for DateTime-based schedules.
    /// </summary>
    public DateTime? ScheduledDateTime { get; private set; }

    /// <summary>
    /// The duration for Duration-based schedules (relative to current time).
    /// </summary>
    public TimeSpan? Duration { get; private set; }

    /// <summary>
    /// Creates a timer schedule for a specific DateTime.
    /// </summary>
    /// <param name="dateTime">The specific DateTime when the timer should execute.</param>
    /// <returns>A WorkflowTimerSchedule configured for DateTime execution.</returns>
    public static TimerSchedule FromDateTime(DateTime dateTime)
    {
        return new TimerSchedule(TimerScheduleType.DateTime, null, dateTime, null);
    }

    /// <summary>
    /// Creates a timer schedule using a duration (time span from now).
    /// </summary>
    /// <param name="duration">The time span from now when the timer should execute.</param>
    /// <returns>A WorkflowTimerSchedule configured for Duration execution.</returns>
    public static TimerSchedule FromDuration(TimeSpan duration)
    {
        return new TimerSchedule(TimerScheduleType.Duration, null, null, duration);
    }

    /// <summary>
    /// Creates a timer schedule for immediate execution.
    /// </summary>
    /// <returns>A WorkflowTimerSchedule configured for immediate execution.</returns>
    public static TimerSchedule Immediate()
    {
        return new TimerSchedule(TimerScheduleType.Immediate, null, null, null);
    }
    
    /// <summary>
    /// Converts this WorkflowTimerSchedule to a DaprJobSchedule for job scheduling.
    /// </summary>
    /// <returns>A DaprJobSchedule equivalent to this WorkflowTimerSchedule.</returns>
    public DaprJobSchedule ToDaprJobSchedule()
    {
        return ScheduleType switch
        {
            TimerScheduleType.DateTime => DaprJobSchedule.FromDateTime(ScheduledDateTime!.Value),
            TimerScheduleType.Duration => DaprJobSchedule.FromDateTime(DateTimeOffset.UtcNow.Add(Duration!.Value)),
            TimerScheduleType.Immediate => DaprJobSchedule.FromDateTime(DateTimeOffset.UtcNow.AddSeconds(2)),
            _ => throw new InvalidOperationException($"Unsupported schedule type: {ScheduleType}")
        };
    }

    /// <summary>
    /// Validates the schedule configuration based on the schedule type.
    /// </summary>
    private void ValidateSchedule()
    {
        switch (ScheduleType)
        {
            case TimerScheduleType.DateTime:
                if (!ScheduledDateTime.HasValue)
                    throw new ArgumentException("ScheduledDateTime is required for DateTime schedule type.");
                break;
                
            case TimerScheduleType.Duration:
                if (!Duration.HasValue)
                    throw new ArgumentException("Duration is required for Duration schedule type.");
                if (Duration.Value <= TimeSpan.Zero)
                    throw new ArgumentException("Duration must be positive.");
                break;
                
            case TimerScheduleType.Immediate:
                // No validation needed for immediate execution
                break;
                
            default:
                throw new ArgumentException($"Unsupported schedule type: {ScheduleType}");
        }
    }

    /// <summary>
    /// Gets the next occurrence of a specific time today or tomorrow.
    /// </summary>
    /// <param name="hour">The hour (0-23).</param>
    /// <param name="minute">The minute (0-59).</param>
    /// <returns>The next DateTime for the specified time.</returns>
    private static DateTime GetNextDailyTime(int hour, int minute)
    {
        var now = DateTime.UtcNow;
        var target = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0, DateTimeKind.Utc);
        
        // If the time has already passed today, schedule for tomorrow
        if (target <= now)
        {
            target = target.AddDays(1);
        }
        
        return target;
    }

    protected override IEnumerable<object> GetAtomicValues()
    {
        yield return ScheduleType;
        yield return Expression ?? string.Empty;
        yield return ScheduledDateTime?.Ticks ?? 0;
        yield return Duration?.Ticks ?? 0;
    }
}
