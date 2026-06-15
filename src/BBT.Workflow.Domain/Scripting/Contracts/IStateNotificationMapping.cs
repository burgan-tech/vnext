namespace BBT.Workflow.Scripting;

/// <summary>
/// Optional enrichment contract for the platform-managed <c>state</c> notification channel.
/// The platform produces the <see cref="NotificationMessage.Data"/> automatically (State Function output).
/// This interface allows user scripts to supply additional <see cref="StateNotificationMetadata.Metadata"/>
/// (e.g. HTTP headers, query strings) and override the Dapr binding <see cref="StateNotificationMetadata.Operation"/>.
/// </summary>
/// <remarks>
/// Implement this interface in the same <c>.csx</c> file as <see cref="INotificationMapping"/>.
/// If the script does not implement this interface, the state channel uses default metadata only.
/// </remarks>
public interface IStateNotificationMapping
{
    /// <summary>
    /// Provides metadata enrichment for the state channel notification.
    /// </summary>
    /// <param name="context">
    /// Workflow runtime context providing access to <c>Headers</c>,
    /// <c>QueryParameters</c>, <c>Instance</c>, <c>Body</c>, and <c>Workflow</c>.
    /// </param>
    /// <returns>Metadata and operation overrides for the state channel Dapr binding.</returns>
    Task<StateNotificationMetadata> EnrichAsync(ScriptContext context);
}

/// <summary>
/// Metadata enrichment result for the state notification channel.
/// The platform produces the <see cref="NotificationMessage.Data"/> -- this type
/// controls only binding-level metadata (HTTP headers, operation, etc.).
/// </summary>
public sealed class StateNotificationMetadata
{
    /// <summary>
    /// Dapr binding metadata dictionary.
    /// For HTTP bindings these entries become HTTP request headers.
    /// </summary>
    public Dictionary<string, string> Metadata { get; init; } = [];

    /// <summary>
    /// Dapr binding operation override. Defaults to <c>"create"</c>.
    /// </summary>
    public string Operation { get; init; } = "create";
}
