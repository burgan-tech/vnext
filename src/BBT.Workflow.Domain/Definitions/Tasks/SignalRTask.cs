using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// SignalR Task Definition
/// </summary>
public sealed class SignalRTask : WorkflowTask
{
    private SignalRTask()
    {
    }

    [JsonConstructor]
    private SignalRTask(
        JsonElement config) : base(config)
    {
        Type = ((int)BBT.Workflow.Definitions.TaskType.SignalR).ToString();
    }

    /// <summary>
    /// SignalR Notification Type
    /// </summary>
    public SignalRTaskType NotificationType { get; private set; } = SignalRTaskType.WorkflowSignalR;

    /// <summary>
    /// Additional Data
    /// </summary>
    public JsonElement? AdditionalData { get; private set; }

    /// <summary>
    /// Allowed headers to be forwarded from context to SignalR request.
    /// If null or empty, no headers will be forwarded.
    /// </summary>
    public List<string>? AllowedHeaders { get; private set; }

    /// <summary>
    /// Sets the additional data for the SignalR task
    /// </summary>
    /// <param name="data">The data to be sent with the SignalR message</param>
    public void SetAdditionalData(dynamic data)
    {
        AdditionalData = JsonSerializer.SerializeToElement(data);
    }

    /// <summary>
    /// Internal property setters for object pooling
    /// </summary>
    internal void SetNotificationTypeInternal(SignalRTaskType notificationType) => NotificationType = notificationType;
    internal void SetAdditionalDataInternal(JsonElement? additionalData) => AdditionalData = additionalData;
    internal void SetAllowedHeadersInternal(List<string>? allowedHeaders) => AllowedHeaders = allowedHeaders;

    protected override void Configure(JsonElement config)
    {
        base.Configure(config);

        if (config.TryGetProperty("notificationType", out var notificationType))
        {
            NotificationType = (SignalRTaskType)notificationType.GetInt32();
        }

        if (config.TryGetProperty("additionalData", out var additionalDataElement))
        {
            var additionalData = additionalDataElement.GetRawText();
            AdditionalData = string.IsNullOrWhiteSpace(additionalData) ? null : additionalDataElement;
        }

        if (config.TryGetProperty("allowedHeaders", out var allowedHeadersElement))
        {
            AllowedHeaders = allowedHeadersElement.Deserialize<List<string>>();
        }
    }

    public static SignalRTask Create(
        JsonElement config)
    {
        return new SignalRTask(config);
    }

    /// <summary>
    /// Creates a deep copy of the current SignalRTask instance.
    /// </summary>
    public override WorkflowTask Clone()
    {
        return CloneTyped();
    }

    /// <summary>
    /// Creates a typed deep copy of the current SignalRTask instance.
    /// </summary>
    public SignalRTask CloneTyped()
    {
        var cloned = new SignalRTask();
        CopyBaseTo(cloned);

        cloned.NotificationType = NotificationType;
        cloned.AdditionalData = AdditionalData;
        cloned.AllowedHeaders = AllowedHeaders != null ? new List<string>(AllowedHeaders) : null;

        return cloned;
    }

    /// <summary>
    /// Internal method for object pooling - copies all properties efficiently
    /// </summary>
    /// <param name="source">Source task to copy from</param>
    public void CopyFromInternal(SignalRTask source)
    {
        source.CopyBaseToInternal(this);
        SetNotificationTypeInternal(source.NotificationType);
        SetAdditionalDataInternal(source.AdditionalData);
        SetAllowedHeadersInternal(source.AllowedHeaders != null ? new List<string>(source.AllowedHeaders) : null);
    }

    /// <summary>
    /// Resets the task instance to a clean state for object pooling
    /// </summary>
    public override void Reset()
    {
        base.Reset();
        NotificationType = SignalRTaskType.WorkflowSignalR;
        AdditionalData = null;
        AllowedHeaders = null;
    }

    /// <summary>
    /// Creates a new instance for object pooling - internal use only
    /// </summary>
    public static SignalRTask CreateEmpty()
    {
        return new SignalRTask();
    }
}

