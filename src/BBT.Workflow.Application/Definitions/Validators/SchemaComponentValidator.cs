using System.Text.Json;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Validates workflow schema components (sys-schemas).
/// Ensures schema definitions are properly structured and contain required fields.
/// </summary>
public sealed class SchemaComponentValidator : IComponentValidator
{
    /// <inheritdoc />
    public bool CanHandle(string componentType) => componentType == RuntimeSysSchemaInfo.Schemas;

    /// <inheritdoc />
    public ComponentValidationResult Validate(JsonElement attributes)
    {
        var result = new ComponentValidationResult();

        try
        {
            var schema = attributes.Deserialize<SchemaDefinition>(JsonSerializerConstants.JsonOptions);
            if (schema == null)
            {
                result.AddError("Failed to deserialize schema from attributes.", nameof(SchemaDefinition));
                return result;
            }

            // Validate required type field
            if (string.IsNullOrWhiteSpace(schema.Type))
            {
                result.AddError("Schema type is required.", $"{nameof(SchemaDefinition)}.{nameof(SchemaDefinition.Type)}");
            }

            // Validate schema definition exists
            if (schema.Schema.ValueKind == JsonValueKind.Undefined || schema.Schema.ValueKind == JsonValueKind.Null)
            {
                result.AddError("Schema definition is required.", $"{nameof(SchemaDefinition)}.{nameof(SchemaDefinition.Schema)}");
                return result;
            }

            ValidateSensitiveAnnotations(schema.Schema, result);

            return result;
        }
        catch (JsonException ex)
        {
            result.AddError($"Invalid JSON format for schema: {ex.Message}", nameof(SchemaDefinition));
            return result;
        }
    }

    /// <summary>
    /// Rejects unusable <c>x-sensitive</c> annotations at publish time. This is the only place a
    /// bad security marker is visible to the author: at runtime an encrypted-and-filterable field
    /// matches nothing without erroring, and an unreachable annotation simply never fires.
    /// </summary>
    private static void ValidateSensitiveAnnotations(JsonElement schemaRoot, ComponentValidationResult result)
    {
        foreach (var problem in SensitiveSchemaParser.Validate(schemaRoot))
        {
            result.AddError(
                $"{SensitiveSchemaParser.SensitiveKey} at '{problem.Path}': {problem.Message}",
                $"{nameof(SchemaDefinition)}.{nameof(SchemaDefinition.Schema)}.{problem.Path}");
        }
    }
}
