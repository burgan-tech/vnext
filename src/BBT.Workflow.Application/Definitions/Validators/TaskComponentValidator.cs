using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Validates workflow task components (sys-tasks).
/// Ensures task definitions are properly structured and contain required fields.
/// </summary>
public sealed partial class TaskComponentValidator : IComponentValidator
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

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex PythonHandlerNamePattern();
}
