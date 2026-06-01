using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Domain;
using Json.Schema;
using Microsoft.Extensions.Logging;

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
    private readonly IReadOnlyDictionary<string, IJsonSchemaCustomValidationRule> _customRules;
    private readonly ILogger<CachedJsonSchemaValidator>? _logger;

    public CachedJsonSchemaValidator(
        IEnumerable<IJsonSchemaCustomValidationRule>? customRules = null,
        ILogger<CachedJsonSchemaValidator>? logger = null)
    {
        _customRules = (customRules ?? [])
            .GroupBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    /// <summary>
    /// Validates the given JSON data against the specified JSON schema using Result Pattern.
    /// The schema is cached based on its content hash to enable fast subsequent validations
    /// and avoid $id conflicts with different schema versions.
    /// </summary>
    /// <param name="jsonSchema">JSON schema to be used for validation</param>
    /// <param name="data">JSON data to be validated. If null, an empty JSON object "{}" is used for validation</param>
    /// <returns>Result containing validation outcome. On failure, Error.ValidationErrors contains detailed field-level errors.</returns>
    public Result Validate(JsonElement jsonSchema, JsonElement? data)
        => Validate(jsonSchema, data, SchemaValidationOptions.Default);

    public Result Validate(JsonElement jsonSchema, JsonElement? data, SchemaValidationOptions options)
    {
        // Compute cache key based on schema content
        var cacheKey = ComputeCacheKey(jsonSchema);
        
        // Get or build schema (thread-safe)
        var schema = _schemaCache.GetOrAdd(cacheKey, _ => BuildSchema(jsonSchema));
        
        // Validate using cached schema
        return ValidateInternal(schema, jsonSchema, data, options, _customRules, _logger);
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
        
        var schemaWithoutVocabulary = JsonSchemaVocabularySanitizer.RemoveVocabularyKeywords(jsonSchema);
        return JsonSchema.Build(schemaWithoutVocabulary, options);
    }

    /// <summary>
    /// Validates data against a pre-built schema and converts results to Result pattern.
    /// Uses hierarchical output format and requires format validation for comprehensive validation.
    /// </summary>
    /// <param name="schema">The pre-built JsonSchema</param>
    /// <param name="data">The data to validate</param>
    /// <returns>Result containing validation outcome</returns>
    private static Result ValidateInternal(
        JsonSchema schema,
        JsonElement jsonSchema,
        JsonElement? data,
        SchemaValidationOptions options,
        IReadOnlyDictionary<string, IJsonSchemaCustomValidationRule> customRules,
        ILogger<CachedJsonSchemaValidator>? logger)
    {
        var json = JsonDocument.Parse(data?.GetRawText() ?? "{}");

        var validationResult = schema.Evaluate(json.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.Hierarchical,
            RequireFormatValidation = true
        });

        var customValidationErrors = options.CustomValidationEnabled
            ? EvaluateCustomRules(jsonSchema, json.RootElement, options, customRules, logger)
            : [];

        if (validationResult.IsValid && customValidationErrors.Count == 0)
        {
            return Result.Ok();
        }

        if (options.IncludeVocabularyDetails)
        {
            var details = validationResult.ToSchemaValidationProblemDetails(jsonSchema, options, customValidationErrors);

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
        validationErrors.AddRange(customValidationErrors.Select(error =>
            new System.ComponentModel.DataAnnotations.ValidationResult(error.Message, [error.Path])));

        return Result.Fail(
            Error.Validation(
                WorkflowErrorCodes.ValidationErrors,
                "JSON schema validation failed",
                validationErrors.AsReadOnly()));
    }

    private static List<SchemaValidationErrorDetail> EvaluateCustomRules(
        JsonElement jsonSchema,
        JsonElement data,
        SchemaValidationOptions options,
        IReadOnlyDictionary<string, IJsonSchemaCustomValidationRule> customRules,
        ILogger<CachedJsonSchemaValidator>? logger)
    {
        var metadata = JsonSchemaVocabularyMetadataResolver.Resolve(jsonSchema);
        var errors = new List<SchemaValidationErrorDetail>();
        EvaluateCustomRulesRecursive(data, string.Empty, metadata, options, customRules, logger, errors);
        return errors;
    }

    private static void EvaluateCustomRulesRecursive(
        JsonElement value,
        string path,
        JsonSchemaVocabularyMetadataResolver metadata,
        SchemaValidationOptions options,
        IReadOnlyDictionary<string, IJsonSchemaCustomValidationRule> customRules,
        ILogger<CachedJsonSchemaValidator>? logger,
        List<SchemaValidationErrorDetail> errors)
    {
        if (!string.IsNullOrEmpty(path) &&
            metadata.FindField(path) is { } field &&
            field.GetValidation() is { } validation &&
            validation.TryGetProperty("rule", out var ruleElement) &&
            ruleElement.ValueKind == JsonValueKind.String)
        {
            var ruleName = ruleElement.GetString();
            if (!string.IsNullOrWhiteSpace(ruleName))
            {
                if (customRules.TryGetValue(ruleName, out var rule))
                {
                    JsonElement? parameters = validation.TryGetProperty("parameters", out var parameterElement)
                        ? parameterElement
                        : null;

                    if (!rule.IsValid(value, parameters))
                    {
                        var message = ResolveCustomRuleMessage(validation, options.EffectiveCulture)
                                      ?? "Validation failed";

                        errors.Add(new SchemaValidationErrorDetail(
                            Path: path,
                            Keyword: "x-validation",
                            Code: $"schema.x-validation.{ruleName}",
                            Message: message,
                            Label: field.ResolveLabel(options.EffectiveCulture),
                            SchemaPath: null,
                            Parameters: parameters?.ValueKind == JsonValueKind.Object
                                ? parameters.Value.EnumerateObject()
                                    .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
                                : new Dictionary<string, JsonElement>(StringComparer.Ordinal)));
                    }
                }
                else
                {
                    logger?.LogWarning(
                        "JSON schema custom validation rule {RuleName} is not registered. Skipping runtime validation for {Path}",
                        ruleName,
                        path);
                }
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var childPath = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";
                EvaluateCustomRulesRecursive(property.Value, childPath, metadata, options, customRules, logger, errors);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var childPath = string.IsNullOrEmpty(path) ? index.ToString() : $"{path}.{index}";
                EvaluateCustomRulesRecursive(item, childPath, metadata, options, customRules, logger, errors);
                index++;
            }
        }
    }

    private static string? ResolveCustomRuleMessage(JsonElement validation, string culture)
    {
        if (!validation.TryGetProperty("errorMessages", out var messages) ||
            messages.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var message = JsonSchemaVocabularyMetadataResolver.ResolveLocalizedText(messages, culture);
        return string.IsNullOrWhiteSpace(message) ? null : message;
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
