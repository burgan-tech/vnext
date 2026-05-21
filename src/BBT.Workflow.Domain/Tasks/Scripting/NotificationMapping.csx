using BBT.Workflow.Scripting;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Tasks.Scripting;

/// <summary>
/// Example notification mapping that handles non-state channels via <see cref="INotificationMapping"/>
/// and enriches the platform-managed <c>state</c> channel metadata via <see cref="IStateNotificationMapping"/>.
/// Implementing both interfaces in the same script lets the executor compile a single
/// <c>.csx</c> file for both purposes — no additional configuration required.
/// </summary>
public class DefaultNotificationMapping : INotificationMapping, IStateNotificationMapping
{
    public Task<NotificationMessage?> ChannelHandler(string channel, ScriptContext context)
    {
        var instance = context.Instance;

        return Task.FromResult<NotificationMessage?>(new NotificationMessage
        {
            Data = new
            {
                id = instance?.Id.ToString(),
                source = "vnext",
                type = "vnext.workflow",
                subject = ResolveSubject(instance),
                data = new
                {
                    instanceId = instance?.Id.ToString(),
                    state = instance?.CurrentState,
                    status = instance?.Status?.ToString()
                }
            }
        });
    }

    public Task<StateNotificationMetadata> EnrichAsync(ScriptContext context)
    {
        var headers = (IDictionary<string, object?>)context.Headers;

        var metadata = new Dictionary<string, string>();

        if (headers.TryGetValue("X-Device-Id", out var deviceId) && deviceId is not null)
            metadata["X-Device-Id"] = deviceId.ToString()!;

        if (headers.TryGetValue("X-Token-Id", out var tokenId) && tokenId is not null)
            metadata["X-Token-Id"] = tokenId.ToString()!;

        return Task.FromResult(new StateNotificationMetadata
        {
            Metadata = metadata
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
