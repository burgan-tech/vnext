using System.Globalization;
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
    public async Task<Result<WorkflowOutputResult?>> ApplyAsync(
        Definitions.Workflow workflow,
        ScriptContext scriptContext,
        CancellationToken cancellationToken = default)
    {
        // Not configured → signal "no output" so the caller keeps the standard envelope.
        if (workflow.Output is null || !workflow.Output.HasMappingCode)
            return Result<WorkflowOutputResult?>.Ok(null);

        try
        {
            var handler = await scriptEngine.CompileToInstanceAsync<IOutputHandler>(
                workflow.Output,
                flowScripts: workflow.Scripts,
                cancellationToken: cancellationToken);

            var response = await handler.OutputHandler(scriptContext);

            // Output ran — Data may legitimately be null (intentional empty response).
            return Result<WorkflowOutputResult?>.Ok(new WorkflowOutputResult(
                (object?)response.Data,
                response.StatusCode,
                NormalizeHeaders((object?)response.Headers)));
        }
        catch (Exception ex)
        {
            logger.WorkflowOutputScriptFailed(workflow.Key, ex);
            // On failure fall back to the standard envelope (non-blocking).
            return Result<WorkflowOutputResult?>.Ok(null);
        }
    }

    /// <summary>
    /// Converts the script's dynamic Headers value to a string dictionary,
    /// mirroring <c>FunctionAppService.NormalizeHeaders</c>.
    /// </summary>
    private static Dictionary<string, string>? NormalizeHeaders(object? value)
    {
        if (value is null)
            return null;

        try
        {
            var headers = value switch
            {
                Dictionary<string, string> typedHeaders => typedHeaders,
                IDictionary<string, object?> objectHeaders => objectHeaders
                    .Where(header => header.Value != null)
                    .ToDictionary(header => header.Key, header => Convert.ToString(header.Value, CultureInfo.InvariantCulture) ?? string.Empty),
                _ => JsonSerializer.Deserialize<Dictionary<string, string>>(JsonSerializer.Serialize(value))
            };

            return headers is { Count: > 0 } ? headers : null;
        }
        catch
        {
            return null;
        }
    }
}
