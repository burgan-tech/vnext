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
            // Same member path as the vocabulary-detail path, so the two response shapes address
            // a field identically ("customer.ownerUserId", not "/customer/ownerUserId").
            var memberName = ToMemberPath(detail.InstanceLocation.ToString());

            var message = "Validation failed";

            if (!detail.IsValid && detail.Errors is not null && detail.Errors.Any())
            {
                message = string.Join(", ", detail.Errors.Select(s => Regex.Unescape(s.Value)));
            }

            validationResults.Add(new ValidationResult(message, [memberName]));
        }

        // NOTE: the root's own errors are NOT re-appended here. They arrive through FlattenErrors
        // like any other node's. This used to carry a compensation loop for the flattening bug
        // that dropped them — it reported the same error twice and, worse, used the KEYWORD
        // ("required") as the member name, which is not a path a client can bind a message to.

        return validationResults;
    }

    /// <summary>
    /// Recursively flattens hierarchical validation errors from JSON schema evaluation results
    /// into the flat list of failing nodes the callers report to the client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A node's errors and its children are <b>independent</b>: in the hierarchical output a
    /// keyword's error sits on the node that owns the keyword, and that same node gains child
    /// <c>Details</c> the moment the schema evaluates any subschema (<c>properties</c>,
    /// <c>additionalProperties</c>, a nested object). So a root-level <c>required</c> failure is
    /// an error ON THE ROOT that sits beside a full set of (possibly valid) child details.
    /// </para>
    /// <para>
    /// Treating the two as alternatives — recurse when there are details, otherwise take the node
    /// — silently discarded the node's own errors. For a schema with <c>additionalProperties:
    /// false</c> and a nested object, the root's <c>required</c> error was the ONLY error there
    /// was, every child was valid, and the caller received a 400 naming nothing at all.
    /// </para>
    /// </remarks>
    /// <param name="result">The evaluation result to flatten</param>
    /// <returns>A flat list of the failing nodes that carry reportable errors.</returns>
    private static List<EvaluationResults> FlattenErrors(EvaluationResults result)
    {
        var list = new List<EvaluationResults>();
        CollectErrors(result, list);
        return list;
    }

    private static void CollectErrors(EvaluationResults result, List<EvaluationResults> collected)
    {
        if (result.IsValid)
            return;

        var startCount = collected.Count;

        // The node's own errors, if it has any — additive, never instead of the children.
        if (result.Errors is { Count: > 0 })
            collected.Add(result);

        if (result.Details is not null)
        {
            foreach (var child in result.Details)
                CollectErrors(child, collected);
        }

        // An invalid subtree that produced nothing reportable (a failing combinator whose errors
        // live neither here nor on any child) still has to surface, or the payload is rejected
        // with an empty error list. The callers render this as a "Validation failed" placeholder.
        if (collected.Count == startCount)
            collected.Add(result);
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
