using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Notification;
using Dapr.Client;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <inheritdoc />
internal sealed class StateNotificationDispatcher(
    DaprClient daprClient,
    INotificationChannelResolver channelResolver,
    IStateChannelMessageBuilder stateChannelBuilder,
    IScriptEngine scriptEngine,
    ILogger<StateNotificationDispatcher> logger) : IStateNotificationDispatcher
{
    private const string StateChannel = "state";

    /// <inheritdoc />
    public async Task<Result> DispatchAsync(
        ScriptContext scriptContext,
        ScriptCode? mapping,
        CancellationToken cancellationToken)
    {
        var instanceId = scriptContext.Instance?.Id ?? Guid.Empty;

        var stateMapping = await TryCompileStateMappingAsync(
            mapping, scriptContext.Workflow?.Scripts, cancellationToken);

        var buildResult = await stateChannelBuilder.BuildAsync(scriptContext, stateMapping, cancellationToken);
        if (!buildResult.IsSuccess)
        {
            logger.StateNotificationFailed(instanceId, buildResult.Error.Message ?? "State channel build failed");
            return Result.Fail(buildResult.Error);
        }

        try
        {
            var message = buildResult.Value!;
            var bindingName = channelResolver.ResolveBindingName(StateChannel);
            var data = JsonSerializer.SerializeToUtf8Bytes(message.Data, options: JsonSerializerConstants.JsonOptions);

            var bindingRequest = new BindingRequest(bindingName, message.Operation) { Data = data };
            bindingRequest.Metadata.TryAdd("Content-Type", "application/json");
            foreach (var kvp in message.Metadata)
                bindingRequest.Metadata.TryAdd(kvp.Key, kvp.Value);

            // Output bindings bypass HttpClient's DiagnosticsHandler, so the remote leg only stays
            // attached to this trace if we hand the component the trace context ourselves.
            DaprTraceMetadata.StampBinding(bindingRequest.Metadata);

            await daprClient.InvokeBindingAsync(bindingRequest, cancellationToken);

            logger.StateNotificationDispatched(instanceId, bindingName);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            logger.StateNotificationFailed(instanceId, ex.Message);
            return Result.Fail(Error.Failure(
                WorkflowErrorCodes.TaskExecution,
                $"State notification dispatch failed: {ex.Message}"));
        }
    }

    private async Task<IStateNotificationMapping?> TryCompileStateMappingAsync(
        ScriptCode? code, ScriptSettings? flowScripts, CancellationToken cancellationToken)
    {
        if (code is null || !code.HasMappingCode)
            return null;

        try
        {
            return await scriptEngine.CompileToInstanceAsync<IStateNotificationMapping>(
                code, flowScripts: flowScripts, cancellationToken: cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
