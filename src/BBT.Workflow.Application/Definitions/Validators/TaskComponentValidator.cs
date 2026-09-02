using System.Text.Json;
using System.Text;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Validates workflow task components (sys-tasks).
/// Ensures task definitions are properly structured and contain required fields.
/// </summary>
public sealed class TaskComponentValidator : IComponentValidator
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
                    cacheAside.KeyExpression,
                    $"{nameof(CacheAsideTask)}.{nameof(CacheAsideTask.KeyExpression)}",
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
        const string scriptMember = $"{nameof(PythonTask)}.{nameof(PythonTask.Script)}";

        if (task.Script is null)
        {
            result.AddError("Python task requires a 'script'.", scriptMember);
        }
        else
        {
            ScriptCodeValidator.Validate(task.Script, scriptMember, result.ValidationErrors);

            if (task.Script.IsReference)
            {
                result.AddError("Python task scripts must be inline; REF encoding is not supported.", scriptMember);
            }

            if (task.Script.Type.Equals(MappingType.Global))
            {
                result.AddError("Python task scripts cannot use the Global mapping type.", scriptMember);
            }

            if (task.Script.Location.Contains('/') || task.Script.Location.Contains('\\'))
            {
                result.AddError(
                    "Python task script location is a diagnostic file name; filesystem paths are not supported.",
                    scriptMember);
            }

            try
            {
                if (!task.Script.IsReference &&
                    Encoding.UTF8.GetByteCount(task.Script.DecodedCode) > PythonTask.MaxCodeBytes)
                {
                    result.AddError(
                        $"Python task script exceeds the {PythonTask.MaxCodeBytes}-byte limit.",
                        scriptMember);
                }
            }
            catch (InvalidOperationException)
            {
                // ScriptCodeValidator already emitted the actionable invalid-Base64 error.
            }
        }

        if (!string.IsNullOrWhiteSpace(task.ExecutionMode) &&
            !PythonExecutionModes.All.Contains(task.ExecutionMode, StringComparer.OrdinalIgnoreCase))
        {
            result.AddError(
                "Python task executionMode must be one of: pythonNet, process, container.",
                $"{nameof(PythonTask)}.{nameof(PythonTask.ExecutionMode)}");
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
}
