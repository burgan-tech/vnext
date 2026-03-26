using System.Text.Json;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Validates workflow function components (sys-functions).
/// Ensures function definitions are properly structured and contain required fields.
/// </summary>
public sealed class FunctionComponentValidator : IComponentValidator
{
    /// <inheritdoc />
    public bool CanHandle(string componentType) => componentType == RuntimeSysSchemaInfo.Functions;

    /// <inheritdoc />
    public ComponentValidationResult Validate(JsonElement attributes)
    {
        var result = new ComponentValidationResult();

        try
        {
            var function = attributes.Deserialize<Function>(JsonSerializerConstants.JsonOptions);
            if (function == null)
            {
                result.AddError("Failed to deserialize function from attributes.", nameof(Function));
                return result;
            }

            // Validate required task field
            if (function.Task == null && !function.OnExecutionTasks.Any())
            {
                result.AddError("Function task or onExecutionTask is required.", $"{nameof(Function)}.{nameof(Function.Task)}");
            }

            if (function.OnExecutionTasks.Any())
            {
                if (function.Output == null)
                {
                    result.AddError("Function output is required.", $"{nameof(Function)}.{nameof(Function.Output)}");
                }
            }

            // Validate scope
            if (function.Scope == default)
            {
                result.AddError("Function scope is required.", $"{nameof(Function)}.{nameof(Function.Scope)}");
            }

            return result;
        }
        catch (JsonException ex)
        {
            result.AddError($"Invalid JSON format for function: {ex.Message}", nameof(Function));
            return result;
        }
    }
}
