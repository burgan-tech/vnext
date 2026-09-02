using System.Text.Json;
using BBT.Aether;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Service for managing SubFlow and SubProcess workflows.
/// SubFlow: Runs on the main flow, preserves main flow state, acts as a wrapper.
/// SubProcess: Creates separate instances via remote calls (unchanged behavior).
/// Uses IInstanceCommandGateway to route between local and remote execution based on target domain.
/// </summary>
/// <param name="instanceCommandGateway">Gateway for instance commands, routes local/remote based on domain.</param>
/// <param name="configuration">Configuration provider for accessing application settings.</param>
/// <param name="scriptEngine">Script engine for compiling and executing mapping scripts.</param>
/// <param name="logger">Logger for SubFlow telemetry.</param>
public sealed class SubflowStarter(
    IInstanceCommandGateway instanceCommandGateway,
    IConfiguration configuration,
    IScriptEngine scriptEngine,
    ILogger<SubflowStarter> logger) : ISubflowStarter
{
    /// <inheritdoc />
    public async Task<Result> StartAsync(
        Definitions.Workflow workflow,
        Instance parentInstance,
        State targetState,
        Transition transition,
        InstanceCorrelation correlation,
        ScriptContext context,
        ExecMode mode,
        CancellationToken cancellationToken = default)
    {
        var subFlowConfig = targetState.SubFlow!;

        // Handle input mapping if mapping is configured
        ScriptResponse? inputMappingResult = null;
        if (subFlowConfig.Mapping != null)
        {
            var mappingResult = await HandleInputMappingAsync(subFlowConfig, workflow, context, cancellationToken);
            if (!mappingResult.IsSuccess)
            {
                return Result.Fail(mappingResult.Error);
            }

            inputMappingResult = mappingResult.Value;
        }

        return await StartSubFlowInternalAsync(
            workflow,
            parentInstance,
            subFlowConfig.Process,
            targetState.Key,
            transition.Key,
            correlation,
            subFlowConfig.Type.Code,
            inputMappingResult,
            subFlowConfig.HasTimeoutOverride ? subFlowConfig.Overrides!.Timeout : null,
            subFlowConfig.Overrides,
            mode,
            cancellationToken);
    }

    /// <summary>
    /// Internal method that contains the common logic for starting SubFlow/SubProcess workflows.
    /// </summary>
    /// <returns>Result indicating success or failure of the SubFlow start operation.</returns>
    private async Task<Result> StartSubFlowInternalAsync(
        Definitions.Workflow workflow,
        Instance parentInstance,
        Reference subFlowReference,
        string stateKey,
        string transitionKey,
        InstanceCorrelation correlation,
        string subFlowTypeCode,
        ScriptResponse? inputMappingResult,
        WorkflowTimeout? timeoutOverride,
        SubFlowOverrides? overrides,
        ExecMode mode,
        CancellationToken cancellationToken)
    {
        using var activity =
            SubFlowActivityHelper.StartActivity($"SubFlow.Start/{subFlowReference.Domain}/{subFlowReference.Key}");
        // Propagate root instance ID: use parent's stored root, or parent itself if parent is the root
        var rootInstanceId = parentInstance.GetRootInstanceId();
        SubFlowActivityHelper.EnrichWithStart(
            activity,
            parentInstance.Id,
            subFlowReference.Domain,
            subFlowReference.Key,
            correlation.SubFlowInstanceId,
            rootInstanceId);
        activity?.SetTag("vnext.subflow.type", subFlowTypeCode == "S" ? "subflow" : "subprocess");
        activity?.SetTag("vnext.subflow.parent.state", stateKey);
        activity?.SetTag("vnext.subflow.parent.transition", transitionKey);

        using (logger.BeginScope(new Dictionary<string, object>
               {
                   [TelemetryConstants.TagNames.Domain] = workflow.Domain,
                   [TelemetryConstants.TagNames.Flow] = workflow.Key,
                   [TelemetryConstants.TagNames.FlowVersion] = workflow.Version,
                   [TelemetryConstants.TagNames.InstanceId] = parentInstance.Id,
                   [TelemetryConstants.TagNames.InstanceKey] = parentInstance.Key ?? "N/A",
                   [TelemetryConstants.TagNames.SubflowInstanceId] = correlation.SubFlowInstanceId,
                   [TelemetryConstants.TagNames.RootInstanceId] = rootInstanceId,
               }))
        {
            // Prepare instance creation input
            var createInstanceInput = new CreateInstanceInput
            {
                Id = correlation.SubFlowInstanceId,
                Callback = configuration["DAPR_APP_ID"],
                Key = parentInstance.Key ?? string.Empty,
                Attributes = inputMappingResult?.Data != null
                    ? JsonSerializer.SerializeToElement(inputMappingResult.Data)
                    : null,
                Tags =
                [
                    $"parent.key:{parentInstance.Key}",
                    $"parent.domain:{workflow.Domain}",
                    $"parent.flow:{workflow.Key}",
                    $"root.instance:{rootInstanceId}"
                ],
                ExtraProperties = new ExtraPropertyDictionary
                {
                    [DomainConsts.MetaDataKeys.Id] = parentInstance.Id,
                    [DomainConsts.MetaDataKeys.Key] = parentInstance.Key ?? string.Empty,
                    [DomainConsts.MetaDataKeys.Domain] = workflow.Domain,
                    [DomainConsts.MetaDataKeys.Flow] = workflow.Key,
                    [DomainConsts.MetaDataKeys.Version] = workflow.Version,
                    [DomainConsts.MetaDataKeys.State] = stateKey,
                    [DomainConsts.MetaDataKeys.Transition] = transitionKey,
                    [DomainConsts.MetaDataKeys.FlowType] = subFlowTypeCode,
                    [DomainConsts.MetaDataKeys.RootInstanceId] = rootInstanceId
                }
            };

            // Apply timeout override from SubFlow config if present
            if (timeoutOverride != null)
            {
                createInstanceInput.ExtraProperties[DomainConsts.MetaDataKeys.TimeoutOverride] =
                    JsonSerializer.Serialize(timeoutOverride);
            }

            // Serialize parent-defined role overrides (transitions + states) for SubFlow to use during state queries.
            // views are excluded as they are hosted/resolved on the parent side.
            if (overrides?.Transitions is { Count: > 0 })
            {
                createInstanceInput.ExtraProperties[DomainConsts.MetaDataKeys.TransitionRoleOverrides] =
                    JsonSerializer.Serialize(overrides.Transitions);
            }

            if (overrides?.States is { Count: > 0 })
            {
                createInstanceInput.ExtraProperties[DomainConsts.MetaDataKeys.StateRoleOverrides] =
                    JsonSerializer.Serialize(overrides.States);
            }

            // Apply additional properties from input mapping if available
            if (inputMappingResult != null)
            {
                if (!inputMappingResult.Key.IsNullOrEmpty())
                {
                    createInstanceInput.Key = inputMappingResult.Key;
                }

                if (!inputMappingResult.Tags.IsNullOrEmpty())
                {
                    var existingTags = createInstanceInput.Tags?.ToList() ?? new List<string>();
                    existingTags.AddRange(inputMappingResult.Tags);
                    createInstanceInput.Tags = existingTags.ToArray();
                }
            }

            // HTTP headers are case-insensitive. Use OrdinalIgnoreCase so a parent/root instance-id
            // supplied by the input mapping (any casing) is REPLACED by the framework-stamped value
            // below instead of producing a duplicate header entry.
            var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (inputMappingResult?.Headers is IDictionary<string, string?> fromMapping)
            {
                foreach (var kv in fromMapping)
                    headers[kv.Key] = kv.Value;
            }

            headers[TelemetryConstants.HeaderNames.ParentInstanceId] = parentInstance.Id.ToString();
            headers[TelemetryConstants.HeaderNames.RootInstanceId] = rootInstanceId.ToString();

            var subFlowStartInput = new StartInstanceInput(
                subFlowReference.Domain,
                subFlowReference.Key,
                subFlowReference.Version,
                sync: mode == ExecMode.Sync
            )
            {
                Instance = createInstanceInput,
                Headers = headers,
                RouteValues = inputMappingResult?.RouteValues ?? new Dictionary<string, string?>(),
                StrictIdempotency = true // Service-to-service call: return 409 if active instance exists
            };

            var startResult = await instanceCommandGateway.StartSubAsync(subFlowStartInput, cancellationToken);

            if (!startResult.IsSuccess)
            {
                var error = startResult.Error;

                SubFlowActivityHelper.SetError(activity, $"{error.Code}: {error.Message}");
                logger.SubFlowStartFailed(
                    subFlowReference.Key,
                    parentInstance.Id,
                    error.Code,
                    error.Message ?? string.Empty);

                return Result.Fail(error);
            }

            SubFlowActivityHelper.SetSuccess(activity);
            logger.SubFlowStarted(subFlowReference.Key, parentInstance.Id);

            return Result.Ok();
        }
    }

    /// <summary>
    /// Handles input mapping for SubFlow/SubProcess by compiling and executing the mapping script.
    /// </summary>
    /// <param name="subFlowConfig">The SubFlow configuration containing mapping information.</param>
    /// <param name="workflow">The parent workflow definition, used to supply flow-level script settings.</param>
    /// <param name="context">The script context for mapping execution.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>Result containing the mapping response or error.</returns>
    private async Task<Result<ScriptResponse?>> HandleInputMappingAsync(
        Definitions.SubFlow subFlowConfig,
        Definitions.Workflow workflow,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        // Determine the appropriate mapping interface based on SubFlow type
        var mappingInterfaceType = subFlowConfig.Type.Code == "S"
            ? typeof(ISubFlowMapping)
            : typeof(ISubProcessMapping);

        return await ResultExtensions.TryAsync<ScriptResponse?>(async ct =>
        {
            using var scriptActivity = ScriptActivityHelper.StartExecuteActivity("subflowInputMapping");

            // Cast to the appropriate mapping interface and execute InputHandler
            if (subFlowConfig.Type.Code == "S")
            {
                var subFlowMapping = await scriptEngine.CompileToInstanceAsync<ISubFlowMapping>(
                    subFlowConfig.Mapping,
                    flowScripts: workflow.Scripts,
                    cancellationToken: ct);
                return await subFlowMapping.InputHandler(context);
            }

            if (subFlowConfig.Type.Code == "P")
            {
                var subProcessMapping = await scriptEngine.CompileToInstanceAsync<ISubProcessMapping>(
                    subFlowConfig.Mapping,
                    flowScripts: workflow.Scripts,
                    cancellationToken: ct);
                return await subProcessMapping.InputHandler(context);
            }

            // If we reach here, casting failed
            throw new InvalidOperationException(
                $"Failed to cast mapping instance to {mappingInterfaceType.Name} for SubFlow type '{subFlowConfig.Type.Code}'");
        }, cancellationToken, ex => WorkflowErrors.SubFlowInputMappingFailed(
            subFlowConfig.Process.Key, ex.Message, ex.StackTrace));
    }
}