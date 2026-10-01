using System.Text.Json;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Validates workflow schema components (sys-schemas).
/// Ensures schema definitions are properly structured and contain required fields, and that the
/// field-exposure vocabulary (<c>x-indexed</c>, <c>x-masking</c>, <c>x-encryption</c>) is one the runtime can honour.
/// </summary>
/// <param name="maskingEngine">
/// Optional: hosts without the Infrastructure module (none today publish components) still validate the
/// structure of <c>x-masking</c>; only the engine-level parameter check is skipped.
/// </param>
/// <param name="encryptionStatus">
/// Optional: without it an <c>x-encryption</c> <c>hash</c>/<c>encrypt</c> declaration is refused, because nothing
/// could apply it.
/// </param>
public sealed class SchemaComponentValidator(
    IFieldMaskingEngine? maskingEngine = null,
    IFieldEncryptionStatus? encryptionStatus = null) : IComponentValidator
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
                    Definitions.Schemas.AttributeIndexDefinition.ValidateSchema(schema.Schema, schema.Type);
                }
                catch (ArgumentException ex) {
                    result.AddError(ex.Message, "schema.x-indexed");
                }

                ValidateMasking(schema, result);
            }

            return result;
        }
        catch (JsonException ex)
        {
            result.AddError($"Invalid JSON format for schema: {ex.Message}", nameof(SchemaDefinition));
            return result;
        }
    }

    private void ValidateMasking(SchemaDefinition schema, ComponentValidationResult result)
    {
        var structural = FieldMaskingDefinition.Validate(schema.Schema, schema.Type);
        foreach (var error in structural)
            result.AddError(error.Message, $"schema.{error.Keyword}");

        // Engine-level checks only on a structurally valid declaration: the lenient parser turns an
        // invalid one into a fail-closed rule that the engine would accept, which says nothing useful.
        // The engine check is also where a hash rule on a host without a configured salt is refused.
        if (structural.Count > 0)
            return;

        foreach (var (path, rule) in SchemaRolesParser.ParseExposure(schema.Schema).PathMaskRules)
        {
            var member = rule.IsEncryption ? "schema.x-encryption" : "schema.x-masking";

            // With SchemaEncryption:EncryptWrites off the funnel would store the value in plaintext (hash and
            // encrypt are both applied on write). Refusing the publish is the only point where that is visible.
            if (rule.IsEncryption && encryptionStatus?.CanEncrypt != true)
            {
                result.AddError(
                    $"Field '{path}': x-encryption type '{rule.Operator}' is applied when data is written, and writes are not " +
                    "protected on this host (SchemaEncryption:EncryptWrites is off); the value would be stored in plaintext.",
                    member);
                continue;
            }

            if (maskingEngine is null)
                continue;

            foreach (var message in maskingEngine.Validate(rule))
                result.AddError($"Field '{path}': {message}", member);
        }
    }
}
