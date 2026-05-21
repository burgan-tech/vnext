using BBT.Aether.Results;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Builds the <see cref="NotificationMessage"/> for the platform-managed <c>state</c> channel.
/// Data is produced from the State Function; metadata can be enriched via an optional
/// <see cref="IStateNotificationMapping"/> script.
/// </summary>
public interface IStateChannelMessageBuilder
{
    /// <summary>
    /// Produces the state notification message.
    /// </summary>
    /// <param name="context">Task executor context with script context and instance data.</param>
    /// <param name="stateMapping">
    /// Optional user-provided enrichment script. When supplied, its
    /// <see cref="IStateNotificationMapping.EnrichAsync"/> result is merged into
    /// the platform message metadata. Pass <c>null</c> for default metadata only.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result<NotificationMessage>> BuildAsync(
        TaskExecutorContext context,
        IStateNotificationMapping? stateMapping,
        CancellationToken cancellationToken);
}
