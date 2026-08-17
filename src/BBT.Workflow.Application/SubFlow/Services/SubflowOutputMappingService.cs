using System.Text.Json;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.SubFlow;

/// <inheritdoc cref="ISubflowOutputMappingService" />
public sealed class SubflowOutputMappingService(
    IInstanceRepository instanceRepository,
    IInstanceDataWriteService instanceDataWriteService,
    IScriptEngine scriptEngine,
    IScriptContextFactory scriptContextFactory,
    IRuntimeInfoProvider runtimeInfoProvider,
    IGuidGenerator guidGenerator,
    ILogger<SubflowOutputMappingService> logger)
    : ISubflowOutputMappingService
{
    /// <inheritdoc />
    public async Task<Result> ApplyAsync(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
        string parentStateKey,
        JsonElement? childInstanceData,
        CancellationToken cancellationToken = default)
    {
        var parentStateResult = parentWorkflow.GetState(parentStateKey);
        if (!parentStateResult.IsSuccess)
            return Result.Ok();

        var parentState = parentStateResult.Value!;
        var subFlowConfig = parentState.SubFlow;
        if (subFlowConfig?.Mapping is null || !subFlowConfig.Mapping.HasMappingCode)
            return Result.Ok();

        try
        {
            logger.SubFlowOutputMappingStarted(parentInstance.Id);

            var scriptContext = await scriptContextFactory.NewBuilder(instanceRepository)
                .WithWorkflow(parentWorkflow)
                .WithInstance(parentInstance)
                .WithRuntime(runtimeInfoProvider)
                .WithBody(childInstanceData?.Deserialize<Dictionary<string, object>>() ?? new Dictionary<string, object>())
                .BuildAsync(cancellationToken);

            var mappingInstance = await scriptEngine.CompileToInstanceAsync<object>(
                subFlowConfig.Mapping,
                flowScripts: parentWorkflow.Scripts,
                cancellationToken: cancellationToken);

            ScriptResponse? outputMappingResult = null;
            if (subFlowConfig.Type.Equals(SubFlowType.SubFlow) && mappingInstance is ISubFlowMapping subFlowMapping)
            {
                outputMappingResult = await subFlowMapping.OutputHandler(scriptContext);
            }

            var hasData = outputMappingResult?.Data != null;
            if (hasData)
            {
                // Persisted IMMEDIATELY — identity computed under the per-instance row lock.
                await instanceDataWriteService.AppendAsync(
                    parentInstance,
                    new JsonData(JsonSerializer.Serialize(outputMappingResult!.Data)),
                    parentState.VersionStrategy,
                    cancellationToken);
            }

            if (scriptContext.Mutations.HasChanges)
            {
                scriptContext.Mutations.ApplyTo(parentInstance);
                await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
            }

            return Result.Ok();
        }
        catch (Exception ex) when (OutputMappingFailureClassifier.IsTransient(ex))
        {
            // Rethrow so the caller's UnitOfWork is never committed: the correlation completion rolls
            // back with the transaction and the delivery is redelivered against unchanged state.
            // Returning Result.Fail here would fault the parent permanently, with nothing to retry it
            // — see docs/superpowers/specs/2026-08-17-script-alc-double-compile-race-design.md §5.4.
            logger.SubFlowOutputMappingTransientFailure(ex, parentInstance.Id);
            throw;
        }
        catch (Exception ex)
        {
            logger.SubFlowOutputMappingFailed(ex, parentInstance.Id);
            return Result.Fail(WorkflowErrors.SubFlowOutputMappingFailed(
                parentInstance.Id,
                ScriptDiagnostics.Explain(ex),
                stackTrace: ex.ToString()));
        }
    }
}
