using System.Text.Json;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Definitions.CastHandlers;

/// <summary>
/// Handles workflow casting operations specifically for system views ("sys-views").
/// This handler deserializes view data from JSON attributes and updates the views cache.
/// </summary>
/// <param name="cacheContext">The domain cache context used for storing view data.</param>
public sealed class ViewWorkflowCastHandler(IDomainCacheContext cacheContext) : IWorkflowCastHandler
{
    /// <summary>
    /// Determines whether this handler can process the specified workflow type.
    /// This handler specifically processes "sys-views" workflows.
    /// </summary>
    /// <param name="workflow">The workflow type identifier to check.</param>
    /// <returns>True if the workflow type is "sys-views"; otherwise, false.</returns>
    public bool CanHandle(string workflow) => workflow == "sys-views";

    /// <summary>
    /// Asynchronously processes view workflow data by deserializing JSON attributes 
    /// and storing the view in the cache context.
    /// </summary>
    /// <param name="reference">The reference object containing view metadata.</param>
    /// <param name="attributes">The JSON element containing the view attributes to be deserialized.</param>
    /// <param name="cancellationToken">The cancellation token to observe during the asynchronous operation.</param>
    /// <returns>A task that represents the asynchronous handling operation.</returns>
    /// <exception cref="JsonException">Thrown when JSON deserialization of view data fails.</exception>
    public async Task HandleAsync(IReference reference, JsonElement attributes, CancellationToken cancellationToken)
    {
        var item = attributes.Deserialize<View>(JsonSerializerConstants.JsonOptions);
        item!.SetReference(reference);
        await cacheContext.Views.SetAsync(item!, awaitDistributedCache: true, cancellationToken);
    }
}