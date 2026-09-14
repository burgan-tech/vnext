using System.Text.Json;
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
            }

            if (schema.Schema.ValueKind == JsonValueKind.Object)
            {
                try {
                    Definitions.Schemas.AttributeIndexDefinition.ValidateSchema(schema.Schema);
                }
                catch (ArgumentException ex) {
                    result.AddError(ex.Message, "schema.x-indexed");
                }
            }

            return result;
        }
        catch (JsonException ex)
        {
            result.AddError($"Invalid JSON format for schema: {ex.Message}", nameof(SchemaDefinition));
            return result;
        }
    }
}
