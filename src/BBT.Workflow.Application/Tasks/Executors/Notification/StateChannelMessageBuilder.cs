using System.Dynamic;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <inheritdoc />
internal sealed class StateChannelMessageBuilder(
    IInstanceQueryGateway instanceQueryGateway,
    ICurrentUser currentUser,
    ILogger<StateChannelMessageBuilder> logger) : IStateChannelMessageBuilder
{
    public async Task<Result<NotificationMessage>> BuildAsync(
        TaskExecutorContext context,
        IStateNotificationMapping? stateMapping,
        CancellationToken cancellationToken)
    {
        var instance = context.ScriptContext.Instance;
        var workflow = context.ScriptContext.Workflow;

        var instanceIdentifier = !string.IsNullOrWhiteSpace(instance?.Key)
            ? instance!.Key
            : instance?.Id.ToString() ?? string.Empty;

        var headers = DynamicToDictionary(context.ScriptContext.Headers);
        var queryParams = DynamicToDictionary(context.ScriptContext.QueryParameters);

        var input = new GetFunctionWithInstanceInput
        {
            Domain = workflow?.Domain ?? string.Empty,
            Workflow = workflow?.Key ?? string.Empty,
            Instance = instanceIdentifier,
            Version = workflow?.Version,
            Extensions = null,
            Headers = headers,
            QueryParams = queryParams,
            Role = currentUser.Roles is { Length: > 0 } ? currentUser.Roles[0] : null
        };

        var stateResult = await instanceQueryGateway.GetFunctionWithStateAsync(input, cancellationToken);
        if (!stateResult.Result.IsSuccess)
        {
            logger.LogWarning(
                "State channel message build failed for instance {InstanceId}: {Error}",
                instance?.Id, stateResult.Result.Error.Message);

            return Result<NotificationMessage>.Fail(Error.Failure(
                WorkflowErrorCodes.TaskExecution,
                $"State channel data fetch failed: {stateResult.Result.Error.Message}"));
        }

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
                var enrichment = await stateMapping.EnrichAsync(context.ScriptContext);
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

        var data = new
        {
            id = instance?.Id.ToString() ?? string.Empty,
            source = "vnext",
            type = "vnext.workflow",
            subject = ResolveSubject(instance),
            data = stateResult.Result.Value!
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

    private static Dictionary<string, string?> DynamicToDictionary(dynamic? value)
    {
        if (value == null)
            return new Dictionary<string, string?>();

        if (value is IDictionary<string, string?> stringDict)
            return new Dictionary<string, string?>(stringDict);

        if (value is IDictionary<string, object?> objectDict)
            return objectDict.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value?.ToString());

        if (value is ExpandoObject expando)
        {
            var dict = (IDictionary<string, object?>)expando;
            return dict.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value?.ToString());
        }

        return new Dictionary<string, string?>();
    }
}
