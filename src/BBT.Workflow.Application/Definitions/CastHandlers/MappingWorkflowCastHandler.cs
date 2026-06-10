using System.Text.Json;
using BBT.Workflow.Caching;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions.CastHandlers;

/// <summary>
/// Handles workflow casting operations specifically for system mappings ("sys-mappings").
/// This handler deserializes mapping (script-library) component data from JSON attributes and
/// updates the mappings cache.
/// </summary>
/// <param name="cacheContext">The domain cache context used for storing mapping data.</param>
public sealed class MappingWorkflowCastHandler(IDomainCacheContext cacheContext) : IWorkflowCastHandler
{
    /// <summary>
    /// Determines whether this handler can process the specified workflow type.
    /// This handler specifically processes "sys-mappings" workflows.
    /// </summary>
    /// <param name="workflow">The workflow type identifier to check.</param>
    /// <returns>True if the workflow type is "sys-mappings"; otherwise, false.</returns>
    public bool CanHandle(string workflow) => workflow == RuntimeSysSchemaInfo.Mappings;

    /// <summary>
    /// Asynchronously processes mapping workflow data by deserializing JSON attributes
    /// and storing the mapping component in the cache context.
    /// </summary>
    /// <param name="reference">The reference object containing mapping metadata.</param>
    /// <param name="attributes">The JSON element containing the mapping attributes to be deserialized.</param>
    /// <param name="cancellationToken">The cancellation token to observe during the asynchronous operation.</param>
    /// <returns>A task that represents the asynchronous handling operation.</returns>
    /// <exception cref="JsonException">Thrown when JSON deserialization of mapping data fails.</exception>
    public async Task HandleAsync(IReference reference, JsonElement attributes, CancellationToken cancellationToken)
    {
        var item = attributes.Deserialize<Mapping>(JsonSerializerConstants.JsonOptions);
        item!.SetReference(reference);
        await cacheContext.Mappings.SetAsync(item!, cancellationToken);
    }
}
