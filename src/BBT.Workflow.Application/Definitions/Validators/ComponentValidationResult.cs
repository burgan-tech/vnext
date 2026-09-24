using System.ComponentModel.DataAnnotations;
using BBT.Aether.Validation;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Represents the result of a component validation operation.
/// Contains a collection of validation errors if the component is invalid.
/// </summary>
public class ComponentValidationResult : IHasValidationErrors
{
    /// <summary>
    /// Gets the collection of validation errors found during validation.
    /// </summary>
    public IList<ValidationResult> ValidationErrors { get; } = new List<ValidationResult>();

    /// <summary>
    /// Gets a value indicating whether the component is valid (no validation errors).
    /// </summary>
    public bool IsValid => !ValidationErrors.Any();

    /// <summary>
    /// Adds a validation error to the result.
    /// </summary>
    /// <param name="validationResult">The validation result containing error details.</param>
    public void AddError(ValidationResult validationResult)
    {
        ValidationErrors.Add(validationResult);
    }

    /// <summary>
    /// Adds a validation error with a message and optional member names.
    /// </summary>
    /// <param name="errorMessage">The error message.</param>
    /// <param name="memberNames">Optional member names associated with the error.</param>
    public void AddError(string errorMessage, params string[] memberNames)
    {
        ValidationErrors.Add(new ValidationResult(errorMessage, memberNames));
    }

    /// <summary>
    /// Non-blocking findings. Never affects <see cref="IsValid"/>.
    /// </summary>
    public IList<ValidationResult> Warnings { get; } = new List<ValidationResult>();

    /// <summary>
    /// Creates a successful validation result with no errors.
    /// </summary>
    /// <returns>A new <see cref="ComponentValidationResult"/> with no errors.</returns>
    public static ComponentValidationResult Success() => new();

    /// <summary>
    /// Creates a validation result from an existing <see cref="WorkflowValidationResult"/>.
    /// </summary>
    /// <param name="workflowResult">The workflow validation result to convert.</param>
    /// <returns>A new <see cref="ComponentValidationResult"/> containing the same errors.</returns>
    public static ComponentValidationResult FromWorkflowValidationResult(WorkflowValidationResult workflowResult)
    {
        var result = new ComponentValidationResult();
        foreach (var error in workflowResult.ValidationErrors)
        {
            result.AddError(error);
        }
        foreach (var warning in workflowResult.Warnings)
        {
            result.Warnings.Add(warning);
        }
        return result;
    }
}
