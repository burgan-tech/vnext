using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Validates workflow task components (sys-tasks).
/// Ensures task definitions are properly structured and contain required fields.
/// </summary>
public sealed partial class TaskComponentValidator(
    Microsoft.Extensions.Options.IOptions<BBT.Workflow.Tasks.Executors.McpOptions>? mcpOptions = null) : IComponentValidator
{
    /// <inheritdoc />
    public bool CanHandle(string componentType) => componentType == RuntimeSysSchemaInfo.Tasks;

    /// <inheritdoc />
    public ComponentValidationResult Validate(JsonElement attributes)
    {
        var result = new ComponentValidationResult();

        try
        {
            var task = attributes.Deserialize<WorkflowTask>(JsonSerializerConstants.JsonOptions);
            if (task == null)
            {
                result.AddError("Failed to deserialize task from attributes.", nameof(WorkflowTask));
                return result;
            }

            // Validate required type field
            if (string.IsNullOrWhiteSpace(task.Type))
            {
                result.AddError("Task type is required.", $"{nameof(WorkflowTask)}.{nameof(WorkflowTask.Type)}");
            }
            else
            {
                // Validate task type is a valid enum value
                if (!Enum.TryParse<TaskType>(task.Type, out _))
                {
                    result.AddError(
                        $"Invalid task type '{task.Type}'. Must be a valid TaskType.",
                        $"{nameof(WorkflowTask)}.{nameof(WorkflowTask.Type)}");
                }
            }

            // CacheAside is the only task type whose own config carries script slots; every other task
            // gets its mapping from the OnExecuteTask that references it, validated with the flow.
            if (task is CacheAsideTask cacheAside)
            {
                ScriptCodeValidator.Validate(
                    cacheAside.SourceMapping,
                    $"{nameof(CacheAsideTask)}.{nameof(CacheAsideTask.SourceMapping)}",
                    result.ValidationErrors);

                ScriptCodeValidator.Validate(
                    cacheAside.KeyScript,
                    $"{nameof(CacheAsideTask)}.key",
                    result.ValidationErrors);
            }

            if (task is PythonTask python)
            {
                ValidatePythonTask(python, result);
            }

            if (task is McpTask mcp)
            {
                ValidateMcpTask(mcp, result);
            }

            return result;
        }
        catch (JsonException ex)
        {
            result.AddError($"Invalid JSON format for task: {ex.Message}", nameof(WorkflowTask));
            return result;
        }
    }

    private static void ValidatePythonTask(PythonTask task, ComponentValidationResult result)
    {
        const string handlerMember = $"{nameof(PythonTask)}.{nameof(PythonTask.Handler)}";

        if (string.IsNullOrWhiteSpace(task.Handler))
        {
            result.AddError("Python task requires a 'handler'.", handlerMember);
        }
        else if (!PythonHandlerNamePattern().IsMatch(task.Handler))
        {
            result.AddError(
                "Python task handler must start with a letter or '_' and contain only letters, digits, '_', '.' or '-' (max 128 characters).",
                handlerMember);
        }

        if (task.TimeoutSeconds is < 1 or > PythonTask.MaxTimeoutSeconds)
        {
            result.AddError(
                $"Python task timeoutSeconds must be between 1 and {PythonTask.MaxTimeoutSeconds}.",
                $"{nameof(PythonTask)}.{nameof(PythonTask.TimeoutSeconds)}");
        }

        if (task.Input is { } input &&
            Encoding.UTF8.GetByteCount(input.GetRawText()) > PythonTask.MaxInputBytes)
        {
            result.AddError(
                $"Python task input exceeds the {PythonTask.MaxInputBytes}-byte limit.",
                $"{nameof(PythonTask)}.{nameof(PythonTask.Input)}");
        }
    }

    /// <summary>
    /// MCP task (type 24, issue #1105): HTTP transport only, an allowed absolute endpoint, a known
    /// operation, and no credential authored as plain text.
    /// </summary>
    private void ValidateMcpTask(McpTask task, ComponentValidationResult result)
    {
        const string prefix = nameof(McpTask);

        if (!string.Equals(task.Transport, McpTransports.Http, StringComparison.OrdinalIgnoreCase))
            result.AddError("MCP task transport must be 'http'.", $"{prefix}.{nameof(McpTask.Transport)}");

        var allowedHosts = mcpOptions?.Value.AllowedHosts ?? [];
        if (!Uri.TryCreate(task.Endpoint, UriKind.Absolute, out _))
        {
            result.AddError("MCP task requires an absolute http(s) 'endpoint'.", $"{prefix}.{nameof(McpTask.Endpoint)}");
        }
        else if (!BBT.Workflow.Tasks.Executors.McpOptions.IsHostAllowed(task.Endpoint, allowedHosts, out var host))
        {
            result.AddError($"MCP task endpoint host '{host}' is not in Mcp:AllowedHosts.", $"{prefix}.{nameof(McpTask.Endpoint)}");
        }

        if (!McpOperations.All.Contains(task.Operation))
            result.AddError($"MCP task operation '{task.Operation}' is not supported.", $"{prefix}.{nameof(McpTask.Operation)}");

        if (task.Operation == McpOperations.ReadResource && string.IsNullOrWhiteSpace(task.Uri))
            result.AddError("MCP task operation 'readResource' requires 'uri'.", $"{prefix}.{nameof(McpTask.Uri)}");

        if (task.Operation == McpOperations.GetPrompt && string.IsNullOrWhiteSpace(task.Prompt))
            result.AddError("MCP task operation 'getPrompt' requires 'prompt'.", $"{prefix}.{nameof(McpTask.Prompt)}");

        if (task.Auth is { } auth && string.IsNullOrWhiteSpace(auth.SecretRef))
            result.AddError("MCP task auth requires 'secretRef'; credentials are never authored inline.", $"{prefix}.{nameof(McpTask.Auth)}");

        foreach (var header in task.PlainTextHeaders)
            result.AddError($"MCP task header '{header}' must be {{ \"secretRef\": ... }}; plain-text header values are not allowed.", $"{prefix}.{nameof(McpTask.Headers)}[{header}]");

        if (task.TimeoutSeconds is < 1 or > McpTask.MaxTimeoutSeconds)
            result.AddError($"MCP task timeoutSeconds must be between 1 and {McpTask.MaxTimeoutSeconds}.", $"{prefix}.{nameof(McpTask.TimeoutSeconds)}");
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PythonHandlerNamePattern();
}
