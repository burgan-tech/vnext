using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Validates workflow flow components (sys-flows).
/// Wraps the existing <see cref="WorkflowValidator"/> for flow-specific validation.
/// </summary>
/// <param name="workflowValidator">The workflow validator to delegate validation to.</param>
public sealed class FlowComponentValidator(WorkflowValidator workflowValidator) : IComponentValidator
{
    /// <inheritdoc />
    public bool CanHandle(string componentType) => componentType == RuntimeSysSchemaInfo.Flows;

    /// <inheritdoc />
    public ComponentValidationResult Validate(JsonElement attributes)
    {
        var result = new ComponentValidationResult();

        try
        {
            var workflow = attributes.Deserialize<Workflow>(JsonSerializerConstants.JsonOptions);
            if (workflow == null)
            {
                result.AddError("Failed to deserialize workflow from attributes.", nameof(Workflow));
                return result;
            }

            var workflowResult = workflowValidator.Validate(workflow);
            return ComponentValidationResult.FromWorkflowValidationResult(workflowResult);
        }
        catch (ArgumentException ex)
        {
            // A malformed RoleGrant (bad shape, blank leaf) throws from its constructor during deserialization.
            result.AddError(ex.Message, nameof(Workflow));
            return result;
        }
        catch (JsonException ex)
        {
            result.AddError($"Invalid JSON format for workflow: {ex.Message}", nameof(Workflow));
            return result;
        }
    }
}
