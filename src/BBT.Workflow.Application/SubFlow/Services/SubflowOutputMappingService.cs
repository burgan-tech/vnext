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
        var preparation = await PrepareAsync(
            parentInstance.Id,
            parentWorkflow,
            parentStateKey,
            cancellationToken);
        if (!preparation.IsSuccess)
            return Result.Fail(preparation.Error);

        return await ApplyPreparedAsync(
            parentInstance,
            parentWorkflow,
            preparation.Value!,
            childInstanceData,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<SubflowOutputMappingPlan>> PrepareAsync(
        Guid parentInstanceId,
        Definitions.Workflow parentWorkflow,
        string parentStateKey,
        CancellationToken cancellationToken = default)
    {
        var parentStateResult = parentWorkflow.GetState(parentStateKey);
        if (!parentStateResult.IsSuccess)
            return Result<SubflowOutputMappingPlan>.Ok(new(null, null));

        var parentState = parentStateResult.Value!;
        var subFlowConfig = parentState.SubFlow;
        if (subFlowConfig?.Mapping is null || !subFlowConfig.Mapping.HasMappingCode)
            return Result<SubflowOutputMappingPlan>.Ok(new(parentState, null));

        try
        {
            var mappingInstance = await scriptEngine.CompileToInstanceAsync<object>(
                subFlowConfig.Mapping,
                flowScripts: parentWorkflow.Scripts,
                cancellationToken: cancellationToken);

            return Result<SubflowOutputMappingPlan>.Ok(new(parentState, mappingInstance));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.SubFlowOutputMappingFailed(ex, parentInstanceId);
            return Result<SubflowOutputMappingPlan>.Fail(WorkflowErrors.SubFlowOutputMappingFailed(
                parentInstanceId,
                ScriptDiagnostics.Explain(ex),
                stackTrace: ex.ToString()));
        }
    }

    /// <inheritdoc />
    public async Task<Result> ApplyPreparedAsync(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
        SubflowOutputMappingPlan plan,
        JsonElement? childInstanceData,
        CancellationToken cancellationToken = default)
    {
        if (plan.ParentState is null || plan.MappingInstance is null)
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

            using var scriptActivity = ScriptActivityHelper.StartExecuteActivity("subflowOutputMapping");

            ScriptResponse? outputMappingResult = null;
            if (plan.ParentState.SubFlow!.Type.Equals(SubFlowType.SubFlow)
                && plan.MappingInstance is ISubFlowMapping subFlowMapping)
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
                    plan.ParentState.VersionStrategy,
                    cancellationToken,
                    parentWorkflow);
            }

            if (scriptContext.Mutations.HasChanges)
            {
                scriptContext.Mutations.ApplyTo(parentInstance);
                await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
            }

            return Result.Ok();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Our own cancellation (shutdown, caller gone) — not a mapping failure. Let it out so the
            // UnitOfWork rolls back; the delivery is redelivered when the runtime is healthy again.
            // A cancellation-shaped exception arriving while OUR token is still live (e.g. a
            // DaprClient call inside the mapping timing out) is a downstream fault, not this case;
            // it falls through to the permanent failed-Result path below.
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
