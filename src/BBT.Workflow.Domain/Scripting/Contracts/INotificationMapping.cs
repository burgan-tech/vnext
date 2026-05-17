namespace BBT.Workflow.Scripting;

/// <summary>
/// Notification task mapping contract.
/// Produces per-channel messages and Dapr binding metadata from the workflow script context.
/// The <c>state</c> channel is handled by the platform and never calls this interface;
/// to enrich state channel metadata, also implement <see cref="IStateNotificationMapping"/>
/// in the same <c>.csx</c> file.
/// </summary>
/// <remarks>
/// Returning <c>null</c> from <see cref="ChannelHandler"/> causes the channel to be
/// skipped (conditional dispatch). This allows a single script to decide at runtime
/// which channels should receive a notification.
/// </remarks>
public interface INotificationMapping
{
    /// <summary>
    /// Produces the message for the specified notification channel.
    /// </summary>
    /// <param name="channel">
    /// Target channel name (e.g. "sms", "email", "push", "hub").
    /// Never "state" -- the platform handles that channel automatically.
    /// </param>
    /// <param name="context">
    /// Workflow runtime context providing access to <c>Headers</c>,
    /// <c>QueryParameters</c>, <c>Instance</c>, <c>Body</c>, and <c>Workflow</c>.
    /// </param>
    /// <returns>
    /// The message to dispatch, or <c>null</c> to skip this channel.
    /// </returns>
    Task<NotificationMessage?> ChannelHandler(string channel, ScriptContext context);
}

/// <summary>
/// Represents a single notification to be dispatched to a Dapr binding.
/// </summary>
public sealed class NotificationMessage
{
    /// <summary>
    /// Payload sent as the Dapr binding request body.
    /// </summary>
    public required object Data { get; init; }

    /// <summary>
    /// Dapr binding metadata dictionary.
    /// For HTTP bindings the entries become HTTP headers;
    /// for MQTT / Kafka the <c>topic</c> key sets the target topic, etc.
    /// </summary>
    public Dictionary<string, string> Metadata { get; init; } = [];

    /// <summary>
    /// Dapr binding operation. Defaults to <c>"create"</c>.
    /// </summary>
    public string Operation { get; init; } = "create";
}
