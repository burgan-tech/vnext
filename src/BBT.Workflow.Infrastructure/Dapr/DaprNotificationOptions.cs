namespace BBT.Workflow.Infrastructure.Dapr;

/// <summary>
/// Configuration options for multi-channel Dapr notification bindings.
/// </summary>
public sealed class DaprNotificationOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "Dapr:Notification";

    /// <summary>
    /// Dapr binding component name prefix.
    /// Each channel resolves to <c>{BindingPrefix}-{channel}</c>.
    /// </summary>
    /// <example>vnext-notification</example>
    public string BindingPrefix { get; set; } = "vnext-notification";

    /// <summary>
    /// Default operation for binding invocation when the mapping does not specify one.
    /// </summary>
    public string DefaultOperation { get; set; } = "create";

    /// <summary>
    /// Timeout in seconds for Dapr binding requests.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}
