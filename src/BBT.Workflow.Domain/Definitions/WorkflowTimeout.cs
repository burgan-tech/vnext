using System.Text.Json.Serialization;
using BBT.Aether;

namespace BBT.Workflow.Definitions;

/// <summary>
/// When the workflow starts, a timer counts down.
/// If the workflow is not completed within this time,
/// it is automatically pulled to the targeted status.
/// </summary>
public sealed class WorkflowTimeout : IHasKey
{
    private WorkflowTimeout()
    {
    }

    [JsonConstructor]
    private WorkflowTimeout(
        string key,
        string target,
        VersionStrategy versionStrategy,
        TimerConfig timer,
        ScriptCode? mapping = null
    )
    {
        Key = Check.NotNullOrWhiteSpace(key, nameof(Key), WorkflowConstants.MaxKeyLength);
        Target = Check.NotNullOrWhiteSpace(target, nameof(Target), StateConstants.MaxKeyLength);
        VersionStrategy = versionStrategy;
        Timer = timer;
        Mapping = mapping;
    }

    /// <summary>
    /// If present, it is the more readable key value of the record.
    /// </summary>
    public string Key { get; private set; }

    /// <summary>
    /// The name of the status to which the flow will be drawn.
    /// This status is expected to be of the finish State type.
    /// </summary>
    public string Target { get; private set; }

    /// <summary>
    /// Version Strategy
    /// </summary>
    public VersionStrategy VersionStrategy { get; private set; }

    public TimerConfig Timer { get; private set; }

    /// <summary>
    /// Optional mapping script for dynamic timeout duration calculation.
    /// When provided, the script is executed at runtime to determine the timeout schedule.
    /// If the mapping fails (compile or runtime error), the static Timer.Duration is used as fallback.
    /// </summary>
    public ScriptCode? Mapping { get; private set; }

    public static WorkflowTimeout Create(
        string key,
        string target,
        string versionStrategy,
        string reset,
        string duration,
        ScriptCode? mapping = null
    )
    {
        return new WorkflowTimeout(
            key,
            target,
            VersionStrategy.FromCode(versionStrategy),
            new TimerConfig(reset, duration),
            mapping
        );
    }
}