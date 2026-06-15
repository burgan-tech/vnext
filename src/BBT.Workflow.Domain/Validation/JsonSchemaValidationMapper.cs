using System.ComponentModel.DataAnnotations;
using System.Text.Json;
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
    public static SchemaValidationProblemDetails ToSchemaValidationProblemDetails(
        this EvaluationResults evaluation,
        JsonElement schemaRoot,
        SchemaValidationOptions options,
        IEnumerable<SchemaValidationErrorDetail>? additionalErrors = null)
    {
        var culture = options.EffectiveCulture;
        var metadata = JsonSchemaVocabularyMetadataResolver.Resolve(schemaRoot);
        var errors = new List<SchemaValidationErrorDetail>();

        if (!evaluation.IsValid)
        {
            foreach (var detail in FlattenErrors(evaluation))
            {
                var path = ToMemberPath(detail.InstanceLocation.ToString());
                if (detail.Errors is null || detail.Errors.Count == 0)
                {
                    errors.Add(new SchemaValidationErrorDetail(
                        Path: path,
                        Keyword: "validation",
                        Code: "schema.validation",
                        Message: "Validation failed",
                        Label: metadata.FindField(path)?.ResolveLabel(culture),
                        SchemaPath: detail.EvaluationPath.ToString(),
                        Parameters: new Dictionary<string, JsonElement>(StringComparer.Ordinal)));
                    continue;
                }

                foreach (var error in detail.Errors)
                {
                    var field = metadata.FindField(path);
                    var message = field?.ResolveErrorMessage(error.Key, culture) ?? Regex.Unescape(error.Value);
                    errors.Add(new SchemaValidationErrorDetail(
                        Path: path,
                        Keyword: error.Key,
                        Code: $"schema.{error.Key}",
                        Message: message,
                        Label: field?.ResolveLabel(culture),
                        SchemaPath: detail.EvaluationPath.ToString(),
                        Parameters: field?.GetKeywordParameters(error.Key) ??
                                    new Dictionary<string, JsonElement>(StringComparer.Ordinal)));
                }
            }
        }

        if (additionalErrors is not null)
            errors.AddRange(additionalErrors);

        return new SchemaValidationProblemDetails(culture, errors);
    }

    public static List<ValidationResult> ToValidationResults(this SchemaValidationProblemDetails details)
    {
        return details.Errors
            .Select(error => new ValidationResult(error.Message, [error.Path]))
            .ToList();
    }

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

    private static string ToMemberPath(string instanceLocation)
    {
        var path = instanceLocation.TrimStart('/');
        if (string.IsNullOrWhiteSpace(path))
            return "root";

        return string.Join(
            ".",
            path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(UnescapePointerSegment));
    }

    private static string UnescapePointerSegment(string segment)
        => segment.Replace("~1", "/", StringComparison.Ordinal)
            .Replace("~0", "~", StringComparison.Ordinal);
}
