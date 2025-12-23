using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Configuration for error notifications.
/// Used when ErrorAction is Notify.
/// </summary>
public sealed record NotificationConfig
{
    /// <summary>
    /// The notification channel to use (e.g., "email", "slack", "teams").
    /// </summary>
    [JsonPropertyName("channel")]
    public string Channel { get; init; } = "default";

    /// <summary>
    /// The recipients of the notification.
    /// Format depends on the channel (e.g., email addresses, channel IDs).
    /// </summary>
    [JsonPropertyName("recipients")]
    public IReadOnlyList<string> Recipients { get; init; } = [];

    /// <summary>
    /// Optional template key for the notification message.
    /// </summary>
    [JsonPropertyName("templateKey")]
    public string? TemplateKey { get; init; }

    /// <summary>
    /// Whether to include error details in the notification.
    /// May be disabled for security/PII reasons.
    /// </summary>
    [JsonPropertyName("includeErrorDetails")]
    public bool IncludeErrorDetails { get; init; } = true;

    /// <summary>
    /// Priority level for the notification (e.g., "low", "normal", "high", "critical").
    /// </summary>
    [JsonPropertyName("priority")]
    public string Priority { get; init; } = "normal";
}

