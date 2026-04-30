using System.Text.Json;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Definitions.CastHandlers;

/// <summary>
/// Handles workflow casting operations specifically for system extensions ("sys-extensions").
/// This handler deserializes extension data from JSON attributes and updates the extensions cache.
/// </summary>
/// <param name="cacheContext">The domain cache context used for storing extension data.</param>
public sealed class ExtensionWorkflowCastHandler(IDomainCacheContext cacheContext) : IWorkflowCastHandler
{
    /// <summary>
    /// Determines whether this handler can process the specified workflow type.
    /// This handler specifically processes "sys-extensions" workflows.
    /// </summary>
    /// <param name="workflow">The workflow type identifier to check.</param>
    /// <returns>True if the workflow type is "sys-extensions"; otherwise, false.</returns>
    public bool CanHandle(string workflow) => workflow == "sys-extensions";

    /// <summary>
    /// Asynchronously processes extension workflow data by deserializing JSON attributes 
    /// and storing the extension in the cache context.
    /// </summary>
    /// <param name="reference">The reference object containing extension metadata.</param>
    /// <param name="attributes">The JSON element containing the extension attributes to be deserialized.</param>
    /// <param name="cancellationToken">The cancellation token to observe during the asynchronous operation.</param>
    /// <returns>A task that represents the asynchronous handling operation.</returns>
    /// <exception cref="JsonException">Thrown when JSON deserialization of extension data fails.</exception>
    public async Task HandleAsync(IReference reference, JsonElement attributes, CancellationToken cancellationToken)
    {
        var item = attributes.Deserialize<Extension>(JsonSerializerConstants.JsonOptions);
        item!.SetReference(reference);
        await cacheContext.Extensions.SetAsync(item!, awaitDistributedCache: true, cancellationToken);
    }
}