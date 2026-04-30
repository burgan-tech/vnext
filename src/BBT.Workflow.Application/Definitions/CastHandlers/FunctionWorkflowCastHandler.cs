using System.Text.Json;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Definitions.CastHandlers;

/// <summary>
/// Handles workflow casting operations specifically for system functions ("sys-functions").
/// This handler deserializes function data from JSON attributes and updates the functions cache.
/// </summary>
/// <param name="cacheContext">The domain cache context used for storing function data.</param>
public sealed class FunctionWorkflowCastHandler(IDomainCacheContext cacheContext) : IWorkflowCastHandler
{
    /// <summary>
    /// Determines whether this handler can process the specified workflow type.
    /// This handler specifically processes "sys-functions" workflows.
    /// </summary>
    /// <param name="workflow">The workflow type identifier to check.</param>
    /// <returns>True if the workflow type is "sys-functions"; otherwise, false.</returns>
    public bool CanHandle(string workflow) => workflow == "sys-functions";

    /// <summary>
    /// Asynchronously processes function workflow data by deserializing JSON attributes 
    /// and storing the function in the cache context.
    /// </summary>
    /// <param name="reference">The reference object containing function metadata.</param>
    /// <param name="attributes">The JSON element containing the function attributes to be deserialized.</param>
    /// <param name="cancellationToken">The cancellation token to observe during the asynchronous operation.</param>
    /// <returns>A task that represents the asynchronous handling operation.</returns>
    /// <exception cref="JsonException">Thrown when JSON deserialization of function data fails.</exception>
    public async Task HandleAsync(IReference reference, JsonElement attributes, CancellationToken cancellationToken)
    {
        var item = attributes.Deserialize<Function>(JsonSerializerConstants.JsonOptions);
        item!.SetReference(reference);
        await cacheContext.Functions.SetAsync(item!, awaitDistributedCache: true, cancellationToken);
    }
}