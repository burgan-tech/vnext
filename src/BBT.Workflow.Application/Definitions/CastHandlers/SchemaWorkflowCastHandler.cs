using System.Text.Json;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Definitions.CastHandlers;

/// <summary>
/// Handles workflow casting operations specifically for system schemas ("sys-schemas").
/// This handler deserializes schema definition data from JSON attributes and updates the schemas cache.
/// </summary>
/// <param name="cacheContext">The domain cache context used for storing schema definition data.</param>
public sealed class SchemaWorkflowCastHandler(IDomainCacheContext cacheContext) : IWorkflowCastHandler
{
    /// <summary>
    /// Determines whether this handler can process the specified workflow type.
    /// This handler specifically processes "sys-schemas" workflows.
    /// </summary>
    /// <param name="workflow">The workflow type identifier to check.</param>
    /// <returns>True if the workflow type is "sys-schemas"; otherwise, false.</returns>
    public bool CanHandle(string workflow) => workflow == "sys-schemas";

    /// <summary>
    /// Asynchronously processes schema workflow data by deserializing JSON attributes 
    /// and storing the schema definition in the cache context.
    /// </summary>
    /// <param name="reference">The reference object containing schema metadata.</param>
    /// <param name="attributes">The JSON element containing the schema definition attributes to be deserialized.</param>
    /// <param name="cancellationToken">The cancellation token to observe during the asynchronous operation.</param>
    /// <returns>A task that represents the asynchronous handling operation.</returns>
    /// <exception cref="JsonException">Thrown when JSON deserialization of schema definition data fails.</exception>
    public async Task HandleAsync(IReference reference, JsonElement attributes, CancellationToken cancellationToken)
    {
        var item = attributes.Deserialize<SchemaDefinition>(JsonSerializerConstants.JsonOptions);
        item!.SetReference(reference);
        await cacheContext.Schemas.SetAsync(item!, awaitDistributedCache: true, cancellationToken);
    }
}