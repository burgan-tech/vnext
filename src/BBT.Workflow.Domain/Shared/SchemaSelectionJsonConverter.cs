using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow;

/// <summary>
/// Custom JSON converter for SchemaSelection. Mirrors <see cref="ViewDefinitionJsonConverter"/>:
/// accepts a bare entry array, a wrapped <c>{ "schemas": [...] }</c> object, a single
/// <c>{ "schema": { ... } }</c> object, or a bare component reference. Always writes the wrapped
/// array form so the persisted shape is canonical.
/// </summary>
public sealed class SchemaSelectionJsonConverter : JsonConverter<SchemaSelection>
{
    /// <inheritdoc />
    public override SchemaSelection? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        // Array format - array of SchemaEntry
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var schemas = JsonSerializer.Deserialize<List<SchemaEntry>>(ref reader, options);
            if (schemas == null || schemas.Count == 0)
                return null;
            return SchemaSelection.CreateWithSchemas(schemas.ToArray());
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            // Wrapped array format
            if (root.TryGetProperty("schemas", out var schemasElement) && schemasElement.ValueKind == JsonValueKind.Array)
            {
                var schemas = JsonSerializer.Deserialize<List<SchemaEntry>>(schemasElement.GetRawText(), options);
                if (schemas == null || schemas.Count == 0)
                    return null;
                return SchemaSelection.CreateWithSchemas(schemas.ToArray());
            }

            // Single wrapped schema
            if (root.TryGetProperty("schema", out var schemaElement) && schemaElement.ValueKind == JsonValueKind.Object)
            {
                var schemaRef = ContractDefinitionJsonReader.ReadReference(schemaElement, options);
                return schemaRef == null ? null : SchemaSelection.CreateDefault(schemaRef);
            }

            // Bare component reference:
            // { "key": ..., "domain": ..., "flow": "sys-schemas", "version": ... }
            if (ContractDefinitionJsonReader.IsBareReference(root))
            {
                var bareRef = ContractDefinitionJsonReader.ReadReference(root, options);
                return bareRef == null ? null : SchemaSelection.CreateDefault(bareRef);
            }

            return null;
        }

        // Unknown format - return null instead of throwing
        return null;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, SchemaSelection value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        // Always write the canonical wrapped array form
        writer.WriteStartObject();
        writer.WritePropertyName("schemas");
        JsonSerializer.Serialize(writer, value.Schemas, options);
        writer.WriteEndObject();
    }
}
