using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Json.Schema;

namespace BBT.Workflow.Validation;

/// <summary>
/// Provides extension methods and mapping functionality to convert JSON schema validation results 
/// into standardized ValidationResult objects. This static class facilitates the transformation 
/// of Json.Schema evaluation results into formats compatible with .NET validation frameworks.
/// </summary>
public static class JsonSchemaValidationMapper
{
    /// <summary>
    /// Converts JSON schema evaluation results into a collection of ValidationResult objects.
    /// This extension method flattens the hierarchical validation errors and maps them to 
    /// standard .NET validation results with appropriate member names and error messages.
    /// </summary>
    /// <param name="evaluation">The evaluation results from JSON schema validation</param>
    /// <returns>
    /// A list of ValidationResult objects representing all validation errors found.
    /// Returns an empty list if the evaluation is valid.
    /// </returns>
    public static List<ValidationResult> ToValidationResults(this EvaluationResults evaluation)
    {
        var validationResults = new List<ValidationResult>();

        if (evaluation.IsValid)
            return validationResults;

        // recursive flattening
        var failedDetails = FlattenErrors(evaluation);

        foreach (var detail in failedDetails)
        {
            var memberName = detail.InstanceLocation.ToString().TrimStart('/');
            if (string.IsNullOrWhiteSpace(memberName)) memberName = "root";

            var message = "Validation failed";

            if (!detail.IsValid && detail.Errors is not null && detail.Errors.Any())
            {
                message = string.Join(", ", detail.Errors.Select(s => Regex.Unescape(s.Value)));
            }

            validationResults.Add(new ValidationResult(message, [memberName]));
        }

        if (!evaluation.IsValid && evaluation.Errors is not null)
        {
            foreach (var error in evaluation.Errors)
            {
                validationResults.Add(new ValidationResult(Regex.Unescape(error.Value), [error.Key]));
            }
        }

        return validationResults;
    }

    /// <summary>
    /// Recursively flattens hierarchical validation errors from JSON schema evaluation results.
    /// This private method traverses the nested structure of evaluation details and collects 
    /// all failed validation nodes into a flat list for easier processing.
    /// </summary>
    /// <param name="result">The evaluation result to flatten</param>
    /// <returns>
    /// A flat list of EvaluationResults containing only the failed validation nodes
    /// </returns>
    private static List<EvaluationResults> FlattenErrors(EvaluationResults result)
    {
        var list = new List<EvaluationResults>();

        if (result is { IsValid: false, Details: not null })
        {
            foreach (var child in result.Details)
            {
                list.AddRange(FlattenErrors(child));
            }
        }
        else if (!result.IsValid)
        {
            list.Add(result);
        }

        return list;
    }
}