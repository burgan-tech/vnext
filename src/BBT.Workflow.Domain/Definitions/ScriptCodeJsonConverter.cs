using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Custom converter for <see cref="ScriptCode"/>. The <c>code</c> field is polymorphic: a string for
/// <see cref="CodeEncoding.Native"/>/<see cref="CodeEncoding.Base64"/>, or a <see cref="Reference"/>
/// object for <see cref="CodeEncoding.Reference"/> (REF). Helper references + sandbox grant are read
/// from the nested <c>scripts</c> object.
/// </summary>
public sealed class ScriptCodeJsonConverter : JsonConverter<ScriptCode>
{
    public override ScriptCode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        var location = TryGet(root, "location") is { ValueKind: JsonValueKind.String } locEl
            ? locEl.GetString()
            : null;

        var type = Deserialize<MappingType>(root, "type", options);
        var encoding = Deserialize<CodeEncoding>(root, "encoding", options) ?? CodeEncoding.Base64;
        var scripts = Deserialize<ScriptSettings>(root, "scripts", options);

        string? code = null;
        Reference? codeReference = null;

        if (TryGet(root, "code") is { } codeEl && codeEl.ValueKind != JsonValueKind.Null)
        {
            if (encoding.Equals(CodeEncoding.Reference))
            {
                // REF: code is a Reference object. Tolerate a string that is not meaningful here.
                if (codeEl.ValueKind == JsonValueKind.Object)
                {
                    codeReference = codeEl.Deserialize<Reference>(options);
                }
            }
            else if (codeEl.ValueKind == JsonValueKind.String)
            {
                code = codeEl.GetString();
            }
        }

        return new ScriptCode(location, code, type, encoding, scripts, codeReference);
    }

    public override void Write(Utf8JsonWriter writer, ScriptCode value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WriteString("location", value.Location);

        writer.WritePropertyName("code");
        if (value.IsReference)
        {
            JsonSerializer.Serialize(writer, value.CodeReference, options);
        }
        else
        {
            writer.WriteStringValue(value.Code);
        }

        writer.WritePropertyName("type");
        JsonSerializer.Serialize(writer, value.Type, options);

        writer.WritePropertyName("encoding");
        JsonSerializer.Serialize(writer, value.Encoding, options);

        if (value.Scripts is not null)
        {
            writer.WritePropertyName("scripts");
            JsonSerializer.Serialize(writer, value.Scripts, options);
        }

        writer.WriteEndObject();
    }

    /// <summary>Case-insensitive property lookup (JSON is camelCase but stays robust).</summary>
    private static JsonElement? TryGet(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static T? Deserialize<T>(JsonElement root, string name, JsonSerializerOptions options)
    {
        var element = TryGet(root, name);
        if (element is null || element.Value.ValueKind == JsonValueKind.Null)
        {
            return default;
        }

        return element.Value.Deserialize<T>(options);
    }
}
