using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow;

/// <summary>
/// Centralized JSON serialization configuration for the entire application.
/// All JSON serialization/deserialization operations should use these options
/// to ensure consistency across Domain, Application, Infrastructure, and API layers.
/// </summary>
public static class JsonSerializerConstants
{
    /// <summary>
    /// The centralized JsonSerializerOptions instance.
    /// Use this for all JSON serialization/deserialization operations.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions;

    static JsonSerializerConstants()
    {
        JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            IncludeFields = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
            MaxDepth = 256
        };

        // Add converters for proper serialization of enums and dynamic objects
        JsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        JsonOptions.Converters.Add(new ExpandoObjectJsonConverter());
    }
}
