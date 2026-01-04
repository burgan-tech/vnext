using System.Text.Json;
using BBT.Aether;
using BBT.Workflow.Definitions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
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
    public async Task StartAsync(
        Definitions.Workflow workflow,
        Instance parentInstance,
        State targetState,
        Transition transition,
        InstanceCorrelation correlation,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        var subFlowConfig = targetState.SubFlow!;

        // Handle input mapping if mapping is configured
        ScriptResponse? inputMappingResult = null;
        if (subFlowConfig.Mapping != null)
        {
            inputMappingResult = await HandleInputMappingAsync(subFlowConfig, context, cancellationToken);
        }

        await StartSubFlowInternalAsync(
            workflow,
            parentInstance,
            subFlowConfig.Process,
            targetState.Key,
            transition.Key,
            correlation,
            subFlowConfig.Type.Code,
            inputMappingResult,
            cancellationToken);
    }

    /// <summary>
    /// Starts a SubProcess workflow without requiring a target state or mapping.
    /// Used for triggering SubProcess workflows from tasks.
    /// </summary>
    /// <param name="workflow">The parent workflow.</param>
    /// <param name="parentInstance">The parent instance.</param>
    /// <param name="subFlowReference">Reference to the SubFlow/SubProcess to start.</param>
    /// <param name="transition">The transition triggering the SubProcess.</param>
    /// <param name="correlation">Correlation information for tracking.</param>
    /// <param name="subFlowType">Type code of the SubFlow ("S" or "P").</param>
    /// <param name="inputMappingResult">Optional input mapping result containing data, headers, and key information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SubStartAsync(
        Definitions.Workflow workflow,
        Instance parentInstance,
        Reference subFlowReference,
        Transition transition,
        InstanceCorrelation correlation,
        string subFlowType,
        ScriptResponse? inputMappingResult = null,
        CancellationToken cancellationToken = default)
    {
        await StartSubFlowInternalAsync(
            workflow,
            parentInstance,
            subFlowReference,
            parentInstance.GetCurrentState,
            transition.Key,
            correlation,
            subFlowType,
            inputMappingResult,
            cancellationToken);
    }

    /// <summary>
    /// Internal method that contains the common logic for starting SubFlow/SubProcess workflows.
    /// </summary>
    private async Task StartSubFlowInternalAsync(
        Definitions.Workflow workflow,
        Instance parentInstance,
        Reference subFlowReference,
        string stateKey,
        string transitionKey,
        InstanceCorrelation correlation,
        string subFlowTypeCode,
        ScriptResponse? inputMappingResult,
        CancellationToken cancellationToken)
    {
        using var activity = SubFlowActivityHelper.StartActivity($"SubFlow.Start/{subFlowReference.Domain}/{subFlowReference.Key}");
        SubFlowActivityHelper.EnrichWithStart(
            activity,
            parentInstance.Id,
            subFlowReference.Domain,
            subFlowReference.Key,
            correlation.SubFlowInstanceId);
        activity?.SetTag("vnext.subflow.type", subFlowTypeCode == "S" ? "subflow" : "subprocess");
        activity?.SetTag("vnext.subflow.parent.state", stateKey);
        activity?.SetTag("vnext.subflow.parent.transition", transitionKey);

        try
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
                    $"parent.flow:{workflow.Key}"
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
                    [DomainConsts.MetaDataKeys.FlowType] = subFlowTypeCode
                }
            };

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

            var subFlowStartInput = new StartInstanceInput(
                subFlowReference.Domain,
                subFlowReference.Key,
                subFlowReference.Version,
                sync: true
            )
            {
                Instance = createInstanceInput,
                Headers = inputMappingResult?.Headers ?? new Dictionary<string, string?>(),
                RouteValues = inputMappingResult?.RouteValues ?? new Dictionary<string, string?>()
            };

            var startResult = await instanceCommandGateway.StartSubAsync(subFlowStartInput, cancellationToken);

            if (!startResult.IsSuccess)
            {
                var error = startResult.Error;

                SubFlowActivityHelper.SetError(activity, $"{error.Code}: {error.Message}");
                logger.LogError(
                    "SubFlow {SubFlowKey} start failed for instance {InstanceId}: {ErrorCode} - {ErrorMessage}",
                    subFlowReference.Key,
                    parentInstance.Id,
                    error.Code,
                    error.Message);

                throw new InvalidOperationException(
                    $"Failed to start SubFlow {subFlowReference.Key}: {error.Message}",
                    new Exception(error.Code));
            }

            SubFlowActivityHelper.SetSuccess(activity);
            logger.LogInformation(
                "SubFlow {SubFlowKey} started successfully for instance {InstanceId}",
                subFlowReference.Key,
                parentInstance.Id);
        }
        catch (Exception ex)
        {
            SubFlowActivityHelper.SetError(activity, ex.Message, ex);
            logger.LogError(ex,
                "SubFlow {SubFlowKey} start failed for instance {InstanceId}",
                subFlowReference.Key,
                parentInstance.Id);

            throw;
        }
    }

    /// <summary>
    /// Handles input mapping for SubFlow/SubProcess by compiling and executing the mapping script.
    /// </summary>
    /// <param name="subFlowConfig">The SubFlow configuration containing mapping information.</param>
    /// <param name="context">The script context for mapping execution.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>The result of the input mapping handler.</returns>
    private async Task<ScriptResponse?> HandleInputMappingAsync(
        Definitions.SubFlow subFlowConfig,
        ScriptContext context,
        CancellationToken cancellationToken = default)
    {
        var mappingCode = subFlowConfig.Mapping.DecodedCode;

        // Determine the appropriate mapping interface based on SubFlow type
        var mappingInterfaceType = subFlowConfig.Type.Code == "S"
            ? typeof(ISubFlowMapping)
            : typeof(ISubProcessMapping);

        // Compile the mapping script to the appropriate interface
        var mappingInstance = await scriptEngine.CompileToInstanceAsync<object>(
            mappingCode,
            cancellationToken: cancellationToken);

        // Cast to the appropriate mapping interface and execute InputHandler
        if (subFlowConfig.Type.Code == "S" && mappingInstance is ISubFlowMapping subFlowMapping)
        {
            return await subFlowMapping.InputHandler(context);
        }

        if (subFlowConfig.Type.Code == "P" && mappingInstance is ISubProcessMapping subProcessMapping)
        {
            return await subProcessMapping.InputHandler(context);
        }

        throw new InvalidOperationException(
            $"Failed to cast mapping instance to {mappingInterfaceType.Name} for SubFlow type '{subFlowConfig.Type.Code}'");
    }
}