using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Notification task definition for multi-channel notification dispatch.
/// Each channel maps to a <c>vnext-notification-{channel}</c> Dapr binding.
/// An <c>INotificationMapping</c> script produces per-channel messages and metadata.
/// The platform-managed <c>state</c> channel sends State Function data automatically.
/// </summary>
public sealed class NotificationTask : WorkflowTask
{
    private NotificationTask()
    {
    }

    [JsonConstructor]
    private NotificationTask(
        JsonElement config) : base(config)
    {
        Type = ((int)TaskType.Notification).ToString();
    }

    /// <summary>
    /// Target channel list: "sms", "email", "push", "hub", "state", etc.
    /// Each channel is resolved to a <c>vnext-notification-{channel}</c> Dapr binding.
    /// </summary>
    public IReadOnlyList<string> Channels { get; private set; } = [];

    /// <summary>
    /// When <c>true</c>, the platform-managed <c>state</c> channel is automatically
    /// included in the dispatch list (uses State Function data, no mapping needed).
    /// </summary>
    public bool IncludeStateChannel { get; private set; } = true;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("channels", out var channelsEl)
            && channelsEl.ValueKind == JsonValueKind.Array)
        {
            Channels = channelsEl.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList()!;
        }

        if (config.TryGetProperty("includeStateChannel", out var stateEl)
            && stateEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            IncludeStateChannel = stateEl.GetBoolean();
        }
    }

    /// <summary>
    /// Creates a new <see cref="NotificationTask"/> from a JSON configuration element.
    /// </summary>
    public static NotificationTask Create(JsonElement config)
    {
        return new NotificationTask(config);
    }

    /// <inheritdoc />
    public override WorkflowTask Clone()
    {
        return CloneTyped();
    }

    /// <summary>
    /// Creates a typed deep copy of the current instance.
    /// </summary>
    public NotificationTask CloneTyped()
    {
        var cloned = new NotificationTask();
        CopyBaseTo(cloned);
        cloned.Channels = Channels;
        cloned.IncludeStateChannel = IncludeStateChannel;
        return cloned;
    }

    /// <summary>
    /// Copies all properties from <paramref name="source"/> for object pooling.
    /// </summary>
    public void CopyFromInternal(NotificationTask source)
    {
        source.CopyBaseToInternal(this);
        Channels = source.Channels;
        IncludeStateChannel = source.IncludeStateChannel;
    }

    /// <inheritdoc />
    public override void Reset()
    {
        base.Reset();
        Channels = [];
        IncludeStateChannel = false;
    }

    /// <summary>
    /// Creates a new empty instance for object pooling.
    /// </summary>
    public static NotificationTask CreateEmpty()
    {
        return new NotificationTask();
    }
}
