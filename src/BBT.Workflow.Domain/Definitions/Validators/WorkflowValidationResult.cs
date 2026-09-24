using System.ComponentModel.DataAnnotations;
using BBT.Aether.Validation;

namespace BBT.Workflow.Definitions.Validators;

public class WorkflowValidationResult: IHasValidationErrors
{
    public IList<ValidationResult> ValidationErrors { get; } = new List<ValidationResult>();

    public bool IsValid => !ValidationErrors.Any();

    public void AddError(ValidationResult validationResult)
    {
        ValidationErrors.Add(validationResult);
    }

    /// <summary>
    /// Findings that do not block publishing (e.g. an override that widens access). Never affects
    /// <see cref="IsValid"/>; logged by the publish path.
    /// </summary>
    public IList<ValidationResult> Warnings { get; } = new List<ValidationResult>();

    public void AddWarning(ValidationResult validationResult)
    {
        Warnings.Add(validationResult);
    }
}