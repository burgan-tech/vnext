using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Custom JSON converter for <see cref="ViewDisplay"/> that supports both the legacy string format
/// (<c>"display": "popup"</c>, interpreted as an SDI hint) and the object format
/// (<c>"display": { "sdi": "popup", "mdi": "tab" }</c>).
/// This ensures backward compatibility with existing view components.
/// </summary>
/// <remarks>
/// Writing mirrors the authored shape: a declaration carrying only an SDI hint is written back as a
/// bare string so component JSON round-trips unchanged; anything declaring an MDI hint is written as
/// an object.
/// </remarks>
public sealed class ViewDisplayJsonConverter : JsonConverter<ViewDisplay>
{
    /// <inheritdoc />
    public override ViewDisplay? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Handle null
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        // Handle legacy string format - the value is an SDI display hint
        if (reader.TokenType == JsonTokenType.String)
        {
            var display = reader.GetString();
            return string.IsNullOrWhiteSpace(display) ? null : ViewDisplay.FromSdi(display);
        }

        // Handle object format - explicit per-mode declaration
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            var sdi = ReadModeValue(root, "sdi", options.PropertyNameCaseInsensitive);
            var mdi = ReadModeValue(root, "mdi", options.PropertyNameCaseInsensitive);

            var display = new ViewDisplay(sdi, mdi);
            return display.IsEmpty ? null : display;
        }

        // Unknown format - treat as absent instead of throwing, consistent with ViewDefinitionJsonConverter
        return null;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ViewDisplay value, JsonSerializerOptions options)
    {
        if (value == null || value.IsEmpty)
        {
            writer.WriteNullValue();
            return;
        }

        // SDI-only declarations round-trip as the legacy string shape
        if (string.IsNullOrWhiteSpace(value.Mdi))
        {
            writer.WriteStringValue(value.Sdi);
            return;
        }

        writer.WriteStartObject();
        if (!string.IsNullOrWhiteSpace(value.Sdi))
            writer.WriteString("sdi", value.Sdi);
        writer.WriteString("mdi", value.Mdi);
        writer.WriteEndObject();
    }

    private static string? ReadModeValue(JsonElement root, string propertyName, bool caseInsensitive)
    {
        if (root.TryGetProperty(propertyName, out var element))
            return element.ValueKind == JsonValueKind.String ? element.GetString() : null;

        if (!caseInsensitive)
            return null;

        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals(propertyName) ||
                string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
        }

        return null;
    }
}
