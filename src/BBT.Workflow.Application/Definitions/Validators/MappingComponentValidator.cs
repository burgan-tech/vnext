using System.Text.Json;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Validates mapping (script-library) components (sys-mappings).
/// Ensures the component carries a name and decodable script code.
/// </summary>
public sealed class MappingComponentValidator : IComponentValidator
{
    /// <inheritdoc />
    public bool CanHandle(string componentType) => componentType == RuntimeSysSchemaInfo.Mappings;

    /// <inheritdoc />
    public ComponentValidationResult Validate(JsonElement attributes)
    {
        var result = new ComponentValidationResult();

        try
        {
            var mapping = attributes.Deserialize<Mapping>(JsonSerializerConstants.JsonOptions);
            if (mapping == null)
            {
                result.AddError("Failed to deserialize mapping from attributes.", nameof(Mapping));
                return result;
            }

            if (string.IsNullOrWhiteSpace(mapping.Name))
            {
                result.AddError("Mapping name is required.", $"{nameof(Mapping)}.{nameof(Mapping.Name)}");
            }

            if (string.IsNullOrWhiteSpace(mapping.Code))
            {
                result.AddError("Mapping code is required.", $"{nameof(Mapping)}.{nameof(Mapping.Code)}");
                return result;
            }

            // A sys-mappings component is the resolution target of a REF mapping; it must itself be
            // plain code (Native/Base64) — REF chaining is not supported.
            if (mapping.Encoding.Equals(CodeEncoding.Reference))
            {
                result.AddError(
                    "Mapping component encoding cannot be REF (a referenced component must be plain Native/Base64 code).",
                    $"{nameof(Mapping)}.{nameof(Mapping.Encoding)}");
                return result;
            }

            // Ensure the code is decodable under its declared encoding so a broken helper
            // fails at publish time rather than at transition time.
            try
            {
                _ = mapping.DecodedCode;
            }
            catch (InvalidOperationException ex)
            {
                result.AddError(ex.Message, $"{nameof(Mapping)}.{nameof(Mapping.Code)}");
            }

            return result;
        }
        catch (JsonException ex)
        {
            result.AddError($"Invalid JSON format for mapping: {ex.Message}", nameof(Mapping));
            return result;
        }
    }
}
