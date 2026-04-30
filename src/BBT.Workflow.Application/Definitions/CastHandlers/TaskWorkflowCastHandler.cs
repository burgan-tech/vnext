using System.Text.Json;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Definitions.CastHandlers;

/// <summary>
/// Handles workflow casting operations specifically for system tasks ("sys-tasks").
/// This handler deserializes workflow task data from JSON attributes and updates the tasks cache.
/// </summary>
/// <param name="cacheContext">The domain cache context used for storing workflow task data.</param>
public sealed class TaskWorkflowCastHandler(IDomainCacheContext cacheContext) : IWorkflowCastHandler
{
    /// <summary>
    /// Determines whether this handler can process the specified workflow type.
    /// This handler specifically processes "sys-tasks" workflows.
    /// </summary>
    /// <param name="workflow">The workflow type identifier to check.</param>
    /// <returns>True if the workflow type is "sys-tasks"; otherwise, false.</returns>
    public bool CanHandle(string workflow) => workflow == "sys-tasks";

    /// <summary>
    /// Asynchronously processes task workflow data by deserializing JSON attributes 
    /// and storing the workflow task in the cache context.
    /// </summary>
    /// <param name="reference">The reference object containing task metadata.</param>
    /// <param name="attributes">The JSON element containing the workflow task attributes to be deserialized.</param>
    /// <param name="cancellationToken">The cancellation token to observe during the asynchronous operation.</param>
    /// <returns>A task that represents the asynchronous handling operation.</returns>
    /// <exception cref="JsonException">Thrown when JSON deserialization of workflow task data fails.</exception>
    public async Task HandleAsync(IReference reference, JsonElement attributes, CancellationToken cancellationToken)
    {
        var item = attributes.Deserialize<WorkflowTask>(JsonSerializerConstants.JsonOptions);
        item!.SetReference(reference);
        await cacheContext.Tasks.SetAsync(item!, awaitDistributedCache: true, cancellationToken);
    }
}