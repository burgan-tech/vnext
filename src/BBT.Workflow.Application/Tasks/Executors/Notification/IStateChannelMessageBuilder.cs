using BBT.Aether.Results;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Builds the <see cref="NotificationMessage"/> for the platform-managed <c>state</c> channel.
/// The data payload is a lightweight pointer (<c>domain</c>, <c>flow</c>, <c>id</c>, <c>version</c>)
/// — consumers re-fetch the full state via the State Function. Metadata can be enriched via an
/// optional <see cref="IStateNotificationMapping"/> script.
/// </summary>
public interface IStateChannelMessageBuilder
{
    /// <summary>
    /// Produces the state notification message from the supplied script context.
    /// </summary>
    /// <param name="scriptContext">Script context carrying the workflow and instance.</param>
    /// <param name="stateMapping">
    /// Optional user-provided enrichment script. When supplied, its
    /// <see cref="IStateNotificationMapping.EnrichAsync"/> result is merged into
    /// the platform message metadata. Pass <c>null</c> for default metadata only.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<Result<NotificationMessage>> BuildAsync(
        ScriptContext scriptContext,
        IStateNotificationMapping? stateMapping,
        CancellationToken cancellationToken);
}
