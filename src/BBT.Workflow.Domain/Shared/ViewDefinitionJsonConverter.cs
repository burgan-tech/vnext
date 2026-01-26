using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow;

/// <summary>
/// Custom JSON converter for ViewDefinition that supports both old "view" format and new "views" array format.
/// This ensures backward compatibility with existing workflows.
/// The converter reads the JSON object and passes it to ViewDefinition's JsonConstructor which handles both formats.
/// </summary>
public sealed class ViewDefinitionJsonConverter : JsonConverter<ViewDefinition>
{
    /// <inheritdoc />
    public override ViewDefinition? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Handle null
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        // Handle array format (new "views" format - array of ViewEntry)
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var views = JsonSerializer.Deserialize<List<ViewEntry>>(ref reader, options);
            if (views == null || views.Count == 0)
                return null;
            return ViewDefinition.CreateWithViews(views.ToArray());
        }

        // Handle object format (old "view" format or wrapped object)
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            // Check for "views" array property
            if (root.TryGetProperty("views", out var viewsElement) && viewsElement.ValueKind == JsonValueKind.Array)
            {
                var views = JsonSerializer.Deserialize<List<ViewEntry>>(viewsElement.GetRawText(), options);
                if (views == null || views.Count == 0)
                    return null;
                return ViewDefinition.CreateWithViews(views.ToArray());
            }

            // Check for "view" object property (old format)
            if (root.TryGetProperty("view", out var viewElement) && viewElement.ValueKind == JsonValueKind.Object)
            {
                var viewRef = JsonSerializer.Deserialize<Reference>(viewElement.GetRawText(), options);
                if (viewRef == null)
                    return null;
                    
                // Check for extensions and loadData
                string[]? extensions = null;
                bool? loadData = null;
                
                if (root.TryGetProperty("extensions", out var extElement))
                    extensions = JsonSerializer.Deserialize<string[]>(extElement.GetRawText(), options);
                if (root.TryGetProperty("loadData", out var loadDataElement))
                    loadData = loadDataElement.GetBoolean();
                    
                return ViewDefinition.CreateDefault(viewRef, extensions, loadData);
            }

            // Empty object - return null
            return null;
        }

        // Unknown format - return null instead of throwing
        return null;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ViewDefinition value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        // Always write in new format ("views" array) for consistency
        writer.WriteStartObject();
        writer.WritePropertyName("views");
        JsonSerializer.Serialize(writer, value.Views, options);
        writer.WriteEndObject();
    }
}
