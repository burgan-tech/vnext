using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Notification;
using Dapr.Client;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Multi-channel notification task executor.
/// Dispatches messages to one or more Dapr bindings (<c>vnext-notification-{channel}</c>)
/// using an <see cref="INotificationMapping"/> script for per-channel message generation
/// and an automatic <c>state</c> channel powered by <see cref="IStateChannelMessageBuilder"/>.
/// If the mapping script also implements <see cref="IStateNotificationMapping"/>,
/// its metadata is merged into the state channel message.
/// Executes locally in the Application layer — does not route through the Execution service.
/// </summary>
public sealed class NotificationTaskExecutor(
    DaprClient daprClient,
    INotificationChannelResolver channelResolver,
    IStateChannelMessageBuilder stateChannelBuilder,
    IScriptEngine scriptEngine,
    ILogger<NotificationTaskExecutor> logger)
    : TaskExecutorBase<NotificationTask>(logger)
{
    private const string StateChannel = "state";

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.Notification;

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        NotificationTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var channels = BuildChannelList(task);
        if (channels.Count == 0)
        {
            Logger.LogWarning("Notification task {TaskKey} has no channels configured", task.Key);
            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
                executionDurationMs: 0,
                taskType: TaskType.ToString()));
        }

        var mappingCode = context.OnExecuteTask.Mapping?.DecodedCode;
        INotificationMapping? mapping = null;
        IStateNotificationMapping? stateMapping = null;

        var hasStateChannel = channels.Any(c => string.Equals(c, StateChannel, StringComparison.OrdinalIgnoreCase));
        var hasNonStateChannels = channels.Any(c => !string.Equals(c, StateChannel, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(mappingCode))
        {
            if (hasNonStateChannels)
                mapping = await scriptEngine.CompileToInstanceAsync<INotificationMapping>(
                    mappingCode, cancellationToken: cancellationToken);

            if (hasStateChannel)
                stateMapping = await TryCompileStateMappingAsync(mappingCode, cancellationToken);
        }

        var dispatched = new List<string>();
        var skipped = new List<string>();
        var errors = new List<string>();

        foreach (var channel in channels)
        {
            var result = await DispatchChannelAsync(
                channel, task, mapping, stateMapping, context, cancellationToken);

            switch (result.Outcome)
            {
                case ChannelOutcome.Dispatched:
                    dispatched.Add(channel);
                    break;
                case ChannelOutcome.Skipped:
                    skipped.Add(channel);
                    break;
                case ChannelOutcome.Failed:
                    errors.Add($"{channel}: {result.Error}");
                    break;
            }
        }

        Logger.NotificationMultiChannelCompleted(
            task.Key,
            context.ScriptContext.Instance?.Id ?? Guid.Empty,
            dispatched.Count, skipped.Count, errors.Count);

        return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
            executionDurationMs: 0,
            taskType: TaskType.ToString(),
            metadata: new Dictionary<string, object>
            {
                ["dispatched"] = dispatched,
                ["skipped"] = skipped,
                ["errors"] = errors
            }));
    }

    private async Task<IStateNotificationMapping?> TryCompileStateMappingAsync(
        string code, CancellationToken cancellationToken)
    {
        try
        {
            return await scriptEngine.CompileToInstanceAsync<IStateNotificationMapping>(
                code,
                cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static List<string> BuildChannelList(NotificationTask task)
    {
        var channels = new List<string>(task.Channels);
        if (task.IncludeStateChannel && !channels.Contains(StateChannel, StringComparer.OrdinalIgnoreCase))
            channels.Add(StateChannel);
        return channels;
    }

    private async Task<ChannelDispatchResult> DispatchChannelAsync(
        string channel,
        NotificationTask task,
        INotificationMapping? mapping,
        IStateNotificationMapping? stateMapping,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            NotificationMessage? message;

            if (string.Equals(channel, StateChannel, StringComparison.OrdinalIgnoreCase))
            {
                var buildResult = await stateChannelBuilder.BuildAsync(
                    context, stateMapping, cancellationToken);
                if (!buildResult.IsSuccess)
                    return ChannelDispatchResult.Fail(buildResult.Error.Message ?? "State channel build failed");
                
                message = buildResult.Value;
            }
            else
            {
                if (mapping is null)
                {
                    return ChannelDispatchResult.Fail(
                        $"Channel '{channel}' requires an INotificationMapping script");
                }

                message = await mapping.ChannelHandler(channel, context.ScriptContext);
                if (message is null)
                {
                    Logger.NotificationChannelSkipped(task.Key, channel,
                        context.ScriptContext.Instance?.Id ?? Guid.Empty);
                    return ChannelDispatchResult.Skip();
                }
            }

            var bindingName = channelResolver.ResolveBindingName(channel);
            var data = JsonSerializer.SerializeToUtf8Bytes(message.Data, options: JsonSerializerConstants.JsonOptions);

            var bindingRequest = new BindingRequest(bindingName, message.Operation) { Data = data };
            bindingRequest.Metadata.TryAdd("Content-Type", "application/json");
            foreach (var kvp in message.Metadata)
                bindingRequest.Metadata.TryAdd(kvp.Key, kvp.Value);

            await daprClient.InvokeBindingAsync(bindingRequest, cancellationToken);

            Logger.NotificationChannelDispatched(task.Key, channel, bindingName,
                context.ScriptContext.Instance?.Id ?? Guid.Empty);

            return ChannelDispatchResult.Ok();
        }
        catch (Exception ex)
        {
            Logger.NotificationChannelFailed(task.Key, channel,
                context.ScriptContext.Instance?.Id ?? Guid.Empty, ex.Message);
            return ChannelDispatchResult.Fail(ex.Message);
        }
    }

    private enum ChannelOutcome { Dispatched, Skipped, Failed }

    private readonly record struct ChannelDispatchResult(ChannelOutcome Outcome, string? Error)
    {
        public static ChannelDispatchResult Ok() => new(ChannelOutcome.Dispatched, null);
        public static ChannelDispatchResult Skip() => new(ChannelOutcome.Skipped, null);
        public static ChannelDispatchResult Fail(string error) => new(ChannelOutcome.Failed, error);
    }
}
