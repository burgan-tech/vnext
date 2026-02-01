using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Domain;
using Json.Schema;

namespace BBT.Workflow.Validation;

/// <summary>
/// Provides cached JSON schema validation functionality using the Json.Schema library and Result Pattern.
/// This implementation caches parsed JsonSchema objects to avoid $id conflicts in the global SchemaRegistry
/// and improve performance by parsing schemas only once.
/// Each schema is built with an isolated SchemaRegistry to prevent conflicts when multiple schema versions
/// share the same $id URN.
/// </summary>
public sealed class CachedJsonSchemaValidator : IJsonSchemaValidator
{
    private readonly ConcurrentDictionary<string, JsonSchema> _schemaCache = new();

    /// <summary>
    /// Validates the given JSON data against the specified JSON schema using Result Pattern.
    /// The schema is cached based on its content hash to enable fast subsequent validations
    /// and avoid $id conflicts with different schema versions.
    /// </summary>
    /// <param name="jsonSchema">JSON schema to be used for validation</param>
    /// <param name="data">JSON data to be validated. If null, an empty JSON object "{}" is used for validation</param>
    /// <returns>Result containing validation outcome. On failure, Error.ValidationErrors contains detailed field-level errors.</returns>
    public Result Validate(JsonElement jsonSchema, JsonElement? data)
    {
        // Compute cache key based on schema content
        var cacheKey = ComputeCacheKey(jsonSchema);
        
        // Get or build schema (thread-safe)
        var schema = _schemaCache.GetOrAdd(cacheKey, _ => BuildSchema(jsonSchema));
        
        // Validate using cached schema
        return ValidateInternal(schema, data);
    }

    /// <summary>
    /// Builds a JsonSchema from a JsonElement using an isolated SchemaRegistry.
    /// This prevents $id conflicts by ensuring each schema has its own registry context.
    /// </summary>
    /// <param name="jsonSchema">The schema definition as JsonElement</param>
    /// <returns>A built JsonSchema ready for evaluation</returns>
    private static JsonSchema BuildSchema(JsonElement jsonSchema)
    {
        // Use BuildOptions with isolated SchemaRegistry to prevent global $id conflicts
        var options = new BuildOptions
        {
            SchemaRegistry = new SchemaRegistry() // Instance-level registry per schema
        };
        
        return JsonSchema.Build(jsonSchema, options);
    }

    /// <summary>
    /// Validates data against a pre-built schema and converts results to Result pattern.
    /// Uses hierarchical output format and requires format validation for comprehensive validation.
    /// </summary>
    /// <param name="schema">The pre-built JsonSchema</param>
    /// <param name="data">The data to validate</param>
    /// <returns>Result containing validation outcome</returns>
    private static Result ValidateInternal(JsonSchema schema, JsonElement? data)
    {
        var json = JsonDocument.Parse(data?.GetRawText() ?? "{}");

        var validationResult = schema.Evaluate(json.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.Hierarchical,
            RequireFormatValidation = true
        });

        if (validationResult.IsValid)
        {
            return Result.Ok();
        }

        var validationErrors = validationResult.ToValidationResults();

        return Result.Fail(
            Error.Validation(
                WorkflowErrorCodes.ValidationErrors,
                "JSON schema validation failed",
                validationErrors.AsReadOnly()));
    }

    /// <summary>
    /// Computes a SHA256-based cache key from the schema content.
    /// This ensures that identical schemas (regardless of $id) share the same cache entry,
    /// while different schemas get different cache entries.
    /// </summary>
    /// <param name="jsonSchema">The schema to hash</param>
    /// <returns>Hexadecimal string representation of the schema's SHA256 hash</returns>
    private static string ComputeCacheKey(JsonElement jsonSchema)
    {
        var schemaText = jsonSchema.GetRawText();
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(schemaText));
        return Convert.ToHexString(hashBytes);
    }
}
