using System.Text.Json;
using BBT.Workflow.Caching;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions.CastHandlers;

/// <summary>
/// Handles publishing of agent components (<c>sys-agents</c>, issue #1105): deserializes the
/// component attributes and stores the agent in the component cache.
/// </summary>
/// <param name="cacheContext">The domain cache context.</param>
public sealed class AgentWorkflowCastHandler(IDomainCacheContext cacheContext) : IWorkflowCastHandler
{
    /// <inheritdoc />
    public bool CanHandle(string workflow) => workflow == RuntimeSysSchemaInfo.Agents;

    /// <inheritdoc />
    public async Task HandleAsync(IReference reference, JsonElement attributes, CancellationToken cancellationToken)
    {
        var item = attributes.Deserialize<Agent>(JsonSerializerConstants.JsonOptions);
        item!.SetReference(reference);
        await cacheContext.Agents.SetAsync(item, cancellationToken);
    }
}
