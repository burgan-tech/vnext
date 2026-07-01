using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances;

/// <inheritdoc cref="IWorkflowOutputMappingService" />
public sealed class WorkflowOutputMappingService(
    IScriptEngine scriptEngine,
    ILogger<WorkflowOutputMappingService> logger)
    : IWorkflowOutputMappingService
{
    /// <inheritdoc />
    public async Task<Result<JsonElement?>> ApplyAsync(
        Definitions.Workflow workflow,
        ScriptContext scriptContext,
        CancellationToken cancellationToken = default)
    {
        if (workflow.Output is null || !workflow.Output.HasMappingCode)
            return Result<JsonElement?>.Ok(null);

        try
        {
            var handler = await scriptEngine.CompileToInstanceAsync<IOutputHandler>(
                workflow.Output,
                flowScripts: workflow.Scripts,
                cancellationToken: cancellationToken);

            var response = await handler.OutputHandler(scriptContext);

            var element = response.Data is null
                ? (JsonElement?)null
                : JsonSerializer.SerializeToElement(response.Data);

            return Result<JsonElement?>.Ok(element);
        }
        catch (Exception ex)
        {
            logger.WorkflowOutputScriptFailed(workflow.Key, ex);
            return Result<JsonElement?>.Ok(null);
        }
    }
}
