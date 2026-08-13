using System.Text.Json;
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

            return result;
        }
        catch (JsonException ex)
        {
            result.AddError($"Invalid JSON format for task: {ex.Message}", nameof(WorkflowTask));
            return result;
        }
    }
}
