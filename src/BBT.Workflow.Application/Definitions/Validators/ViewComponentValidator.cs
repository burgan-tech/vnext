using System.Text.Json;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Validates workflow view components (sys-views).
/// Ensures view definitions are properly structured and contain required fields.
/// </summary>
public sealed class ViewComponentValidator : IComponentValidator
{
    /// <inheritdoc />
    public bool CanHandle(string componentType) => componentType == RuntimeSysSchemaInfo.Views;

    /// <inheritdoc />
    public ComponentValidationResult Validate(JsonElement attributes)
    {
        var result = new ComponentValidationResult();

        try
        {
            var view = attributes.Deserialize<View>(JsonSerializerConstants.JsonOptions);
            if (view == null)
            {
                result.AddError("Failed to deserialize view from attributes.", nameof(View));
                return result;
            }
            
            // Validate view type
            if (view.Type == default)
            {
                result.AddError("View type is required.", $"{nameof(View)}.{nameof(View.Type)}");
            }

            // Validate display
            if (string.IsNullOrWhiteSpace(view.Display))
            {
                result.AddError("View display is required.", $"{nameof(View)}.{nameof(View.Display)}");
            }

            if (!string.IsNullOrEmpty(view.Renderer) && view.Type != ViewType.Json)
            {
                result.AddError(
                    "Renderer can only be set when view type is Json.",
                    $"{nameof(View)}.{nameof(View.Renderer)}");
            }

            return result;
        }
        catch (JsonException ex)
        {
            result.AddError($"Invalid JSON format for view: {ex.Message}", nameof(View));
            return result;
        }
    }
}
