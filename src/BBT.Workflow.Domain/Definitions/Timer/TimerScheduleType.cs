using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions.Timer;

/// <summary>
/// Defines the types of timer schedules supported by the workflow engine.
/// This enum provides compatibility with Dapr job scheduling capabilities.
/// </summary>
[JsonConverter(typeof(StringToEnumJsonConverter<TimerScheduleType>))]
public enum TimerScheduleType
{
    /// <summary>
    /// Schedule execution at a specific DateTime.
    /// Equivalent to DaprJobSchedule.FromDateTime().
    /// </summary>
    DateTime = 1,
    
    /// <summary>
    /// Schedule execution using a time span/duration from now.
    /// Equivalent to DaprJobSchedule.FromDuration().
    /// </summary>
    Duration = 3,
    
    /// <summary>
    /// Schedule execution immediately.
    /// Useful for testing or immediate execution scenarios.
    /// </summary>
    Immediate = 4
}
