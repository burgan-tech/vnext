using System.Text.Json;
using BBT.Aether.Results;
using Json.Schema;
using BBT.Workflow.Domain;

namespace BBT.Workflow.Validation;

/// <summary>
/// Provides JSON schema validation functionality using the Json.Schema library and Result Pattern.
/// This sealed class implements IJsonSchemaValidator and validates JSON data against JSON Schema specifications.
/// Validation errors are returned as Result with detailed field-level error information.
/// </summary>
public sealed class JsonSchemaValidator : IJsonSchemaValidator
{
    public Result Validate(JsonElement jsonSchema, JsonElement? data)
        => Validate(jsonSchema, data, SchemaValidationOptions.Default);

    /// <summary>
    /// Validates the given JSON data against the specified JSON schema using Result Pattern.
    /// Uses hierarchical output format and requires format validation for comprehensive validation.
    /// Returns Result.Ok() on success, or Result.Fail() with detailed validation errors on failure.
    /// </summary>
    /// <param name="jsonSchema">JSON schema to be used for validation</param>
    /// <param name="data">JSON data to be validated. If null, an empty JSON object "{}" is used for validation</param>
    /// <returns>Result containing validation outcome. On failure, Error.ValidationErrors contains detailed field-level errors.</returns>
    public Result Validate(JsonElement jsonSchema, JsonElement? data, SchemaValidationOptions options)
    {
        var schemaWithoutVocabulary = JsonSchemaVocabularySanitizer.RemoveVocabularyKeywords(jsonSchema);
        var schema = JsonSchema.FromText(schemaWithoutVocabulary.GetRawText());
        var json = JsonDocument.Parse(data?.GetRawText() ?? "{}");

        var validationResult = schema.Evaluate(json.RootElement, new EvaluationOptions()
        {
            OutputFormat = OutputFormat.Hierarchical,
            RequireFormatValidation = true
        });
        
        if (validationResult.IsValid)
        {
            return Result.Ok();
        }

        if (options.IncludeVocabularyDetails)
        {
            var details = validationResult.ToSchemaValidationProblemDetails(jsonSchema, options);
            return Result.Fail(
                Error.Validation(
                    WorkflowErrorCodes.ValidationErrors,
                    "JSON schema validation failed",
                    details.ToValidationResults().AsReadOnly())
                    with
                    {
                        Detail = JsonSerializer.Serialize(details)
                    });
        }

        var validationErrors = validationResult.ToValidationResults();

        return Result.Fail(
            Error.Validation(
                WorkflowErrorCodes.ValidationErrors,
                "JSON schema validation failed",
                validationErrors.AsReadOnly()));
    }
}
