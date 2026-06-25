using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <inheritdoc />
internal sealed class StateChannelMessageBuilder(
    ILogger<StateChannelMessageBuilder> logger) : IStateChannelMessageBuilder
{
    public async Task<Result<NotificationMessage>> BuildAsync(
        ScriptContext scriptContext,
        IStateNotificationMapping? stateMapping,
        CancellationToken cancellationToken)
    {
        var instance = scriptContext.Instance;
        var workflow = scriptContext.Workflow;

        var metadata = new Dictionary<string, string>
        {
            ["instanceId"] = instance?.Id.ToString() ?? string.Empty,
            ["state"] = instance?.CurrentState ?? string.Empty
        };

        var operation = "create";

        if (stateMapping is not null)
        {
            try
            {
                var enrichment = await stateMapping.EnrichAsync(scriptContext);
                foreach (var kvp in enrichment.Metadata)
                    metadata[kvp.Key] = kvp.Value;

                if (!string.IsNullOrEmpty(enrichment.Operation))
                    operation = enrichment.Operation;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "IStateNotificationMapping.EnrichAsync failed for instance {InstanceId}. Using default metadata.",
                    instance?.Id);
            }
        }

        // Slim "pointer" payload: consumers re-fetch the full state via the State Function.
        var data = new
        {
            id = instance?.Id.ToString() ?? string.Empty,
            source = "vnext",
            type = "vnext.workflow",
            subject = ResolveSubject(instance),
            data = new
            {
                domain = workflow?.Domain ?? string.Empty,
                flow = workflow?.Key ?? string.Empty,
                id = instance?.Id.ToString() ?? string.Empty,
                version = workflow?.Version ?? string.Empty
            }
        };

        return Result<NotificationMessage>.Ok(new NotificationMessage
        {
            Data = data,
            Metadata = metadata,
            Operation = operation
        });
    }

    private static string ResolveSubject(Instance? instance)
    {
        if (instance is null) return "workflow-state-change";
        if (InstanceStatus.Completed.Equals(instance.Status)) return "workflow-completed";
        if (InstanceStatus.Faulted.Equals(instance.Status)) return "workflow-faulted";
        return "workflow-state-change";
    }
}
