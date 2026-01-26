using System.Text.Json;
using System.Text.Json.Serialization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Tasks.Scripting;

/// <summary>
/// Default mapping implementation for NotificationTask.
/// Sets the notification body with workflow state information including
/// available transitions, correlations, and instance data links.
/// </summary>
public class NotificationMapping : IMapping
{
    public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
    {
        var notifyTask = task as NotificationTask 
            ?? throw new InvalidOperationException("Task must be a NotificationTask");

        var messageData = TryGetStateFromBody(context);

        // Create the CloudEvents-style notification message
        var message = NotificationMessage.Create(
            id: context.Instance.Id.ToString(),
            data: messageData);

        // Set the notification properties on the task
        notifyTask.SetBody(message);
        
        return Task.FromResult(new ScriptResponse());
    }

    public Task<ScriptResponse> OutputHandler(ScriptContext context)
    {
        return Task.FromResult(new ScriptResponse());
    }

    private static object? TryGetStateFromBody(ScriptContext context)
    {
        if (context.Body == null)
        {
            return null;
        }

        if (context.Body is JsonElement bodyElement &&
            bodyElement.ValueKind == JsonValueKind.Object &&
            bodyElement.TryGetProperty("state", out var stateElement))
        {
            return stateElement;
        }

        try
        {
            return context.Body.state;
        }
        catch
        {
            // ignored
        }

        return null;
    }
}

/// <summary>
/// Represents a notification message to be sent through notification channels (SignalR, MQTT, etc.)
/// </summary>
public class NotificationMessage
{
    /// <summary>
    /// Gets or sets the unique identifier of the message
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; set; }
    
    /// <summary>
    /// Gets or sets the source of the message
    /// </summary>
    [JsonPropertyName("source")]
    public required string Source { get; set; }

    /// <summary>
    /// Gets or sets the type of the message
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    /// <summary>
    /// Gets or sets the subject of the message
    /// </summary>
    [JsonPropertyName("subject")]
    public required string Subject { get; set; }

    /// <summary>
    /// Gets or sets the data payload of the message
    /// </summary>
    [JsonPropertyName("data")]
    public required object Data { get; set; }

    /// <summary>
    /// Creates a new notification message with the specified identifier and data.
    /// The source, type, and subject are set to default values: "vnext", "vnext.workflow", and "workflow-completed" respectively.
    /// </summary>
    /// <param name="id">The unique identifier of the message.</param>
    /// <param name="data">The data payload of the message.</param>
    /// <returns>A new instance of NotificationMessage.</returns>
    public static NotificationMessage Create(string id, object data)
    {
        return new NotificationMessage
        {
            Id = id,
            Source = "vnext",
            Type = "vnext.workflow",
            Subject = "workflow-completed",
            Data = data
        };
    }
}

internal class NotificationStateOutput
{
    /// <summary>
    /// Data href link with optional extensions
    /// </summary>
    public DataHref Data { get; set; } = new();

    /// <summary>
    /// View href link
    /// </summary>
    public ViewHref View { get; set; } = new();

    /// <summary>
    /// Current state of the instance
    /// </summary>
    public string State { get; set; } = string.Empty;

    /// <summary>
    /// Instance status
    /// </summary>
    public InstanceStatus? Status { get; set; }

    /// <summary>
    /// Active correlations with href links
    /// </summary>
    public List<ActiveCorrelationHref> ActiveCorrelations { get; set; } = [];

    /// <summary>
    /// Available transition items with href links
    /// </summary>
    public List<TransitionItem> Transitions { get; set; } = [];

    /// <summary>
    /// ETag from the latest instance data
    /// </summary>
    public string ETag { get; set; } = string.Empty;
}