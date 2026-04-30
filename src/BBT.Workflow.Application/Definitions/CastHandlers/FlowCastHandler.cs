using System.Text.Json;
using BBT.Workflow.Caching;

namespace BBT.Workflow.Definitions.CastHandlers;

/// <summary>
/// Handles workflow casting operations specifically for system flows ("sys-flows").
/// This handler deserializes workflow data from JSON attributes and updates the workflows cache.
/// </summary>
/// <param name="cacheContext">The domain cache context used for storing workflow data.</param>
public sealed class FlowCastHandler(IDomainCacheContext cacheContext) : IWorkflowCastHandler
{
    /// <summary>
    /// Determines whether this handler can process the specified workflow type.
    /// This handler specifically processes "sys-flows" workflows.
    /// </summary>
    /// <param name="workflow">The workflow type identifier to check.</param>
    /// <returns>True if the workflow type is "sys-flows"; otherwise, false.</returns>
    public bool CanHandle(string workflow) => workflow == "sys-flows";

    /// <summary>
    /// Asynchronously processes flow workflow data by deserializing JSON attributes 
    /// and storing the workflow in the cache context.
    /// </summary>
    /// <param name="reference">The reference object containing workflow metadata.</param>
    /// <param name="attributes">The JSON element containing the workflow attributes to be deserialized.</param>
    /// <param name="cancellationToken">The cancellation token to observe during the asynchronous operation.</param>
    /// <returns>A task that represents the asynchronous handling operation.</returns>
    /// <exception cref="JsonException">Thrown when JSON deserialization of workflow data fails.</exception>
    public async Task HandleAsync(IReference reference, JsonElement attributes, CancellationToken cancellationToken)
    {
        var item = attributes.Deserialize<Workflow>(JsonSerializerConstants.JsonOptions);
        item!.SetReference(reference);
        await cacheContext.Workflows.SetAsync(item!, awaitDistributedCache: true, cancellationToken);
    }
}