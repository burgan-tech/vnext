using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Domain;

namespace BBT.Workflow.Validation;

/// <summary>
/// Provides JSON schema validation functionality using Result Pattern.
/// </summary>
public interface IJsonSchemaValidator
{
    /// <summary>
    /// Validates the given JSON data against the specified JSON schema using Result Pattern.
    /// Returns Result.Ok() if validation succeeds, or Result.Fail() with detailed validation errors if it fails.
    /// </summary>
    /// <param name="jsonSchema">JSON schema to be used for validation.</param>
    /// <param name="data">JSON data to be validated. In case of null, an empty JSON object is used.</param>
    /// <returns>Result containing validation outcome. On failure, Error.ValidationErrors contains detailed field-level errors.</returns>
    Result Validate(JsonElement jsonSchema, JsonElement? data);

    /// <summary>
    /// Validates the given JSON data against the specified JSON schema using vocabulary-aware options.
    /// </summary>
    /// <param name="jsonSchema">JSON schema to be used for validation.</param>
    /// <param name="data">JSON data to be validated. In case of null, an empty JSON object is used.</param>
    /// <param name="options">Validation options controlling localization and custom validation.</param>
    /// <returns>Result containing validation outcome.</returns>
    Result Validate(JsonElement jsonSchema, JsonElement? data, SchemaValidationOptions options);
}
