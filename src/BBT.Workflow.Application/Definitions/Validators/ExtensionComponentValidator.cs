using System.Text.Json;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Validates workflow extension components (sys-extensions).
/// Ensures extension definitions are properly structured and contain required fields.
/// </summary>
public sealed class ExtensionComponentValidator : IComponentValidator
{
    /// <inheritdoc />
    public bool CanHandle(string componentType) => componentType == RuntimeSysSchemaInfo.Extensions;

    /// <inheritdoc />
    public ComponentValidationResult Validate(JsonElement attributes)
    {
        var result = new ComponentValidationResult();

        try
        {
            var extension = attributes.Deserialize<Extension>(JsonSerializerConstants.JsonOptions);
            if (extension == null)
            {
                result.AddError("Failed to deserialize extension from attributes.", nameof(Extension));
                return result;
            }

            // Validate required type field
            if (extension.Type == default)
            {
                result.AddError("Extension type is required.", $"{nameof(Extension)}.{nameof(Extension.Type)}");
            }

            // Validate required scope field
            if (extension.Scope == default)
            {
                result.AddError("Extension scope is required.", $"{nameof(Extension)}.{nameof(Extension.Scope)}");
            }

            // Validate required task field
            if (extension.Task == null)
            {
                result.AddError("Extension task is required.", $"{nameof(Extension)}.{nameof(Extension.Task)}");
            }
            else
            {
                ScriptCodeValidator.Validate(
                    extension.Task.Mapping,
                    $"{nameof(Extension)}.{nameof(Extension.Task)}.{nameof(OnExecuteTask.Mapping)}",
                    result.ValidationErrors);
            }

            return result;
        }
        catch (JsonException ex)
        {
            result.AddError($"Invalid JSON format for extension: {ex.Message}", nameof(Extension));
            return result;
        }
    }
}
