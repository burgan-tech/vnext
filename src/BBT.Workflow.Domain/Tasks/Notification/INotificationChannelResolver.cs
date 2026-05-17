namespace BBT.Workflow.Tasks.Notification;

/// <summary>
/// Resolves a notification channel name to a Dapr binding component name
/// using the convention <c>{prefix}-{channel}</c>.
/// </summary>
public interface INotificationChannelResolver
{
    /// <summary>
    /// Maps a channel name (e.g. "sms") to its Dapr binding component name
    /// (e.g. "vnext-notification-sms").
    /// </summary>
    string ResolveBindingName(string channel);
}
