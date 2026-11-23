using BBT.Workflow.Scripting;

namespace System.Text.Json;

/// <summary>
/// Extension methods for ScriptContext to provide safe serialization and conversion operations.
/// </summary>
public static class ScriptContextExtensions
{
    /// <summary>
    /// Safely converts the script context body to JsonElement for use in remote instance creation.
    /// </summary>
    /// <param name="context">The script context.</param>
    /// <returns>A JsonElement representation of the body, or empty JsonElement if conversion fails.</returns>
    public static JsonElement GetBodyAsJsonElement(this ScriptContext context)
    {
        try
        {
            if (context.Body == null)
                return CreateEmptyJsonObject();

            // If Body is already a JsonElement, check if it's valid
            if (context.Body is JsonElement jsonElement)
            {
                // Handle JsonElement.Undefined case
                if (jsonElement.ValueKind == JsonValueKind.Undefined)
                    return CreateEmptyJsonObject();
                
                return jsonElement;
            }

            // If Body is a string (JSON), parse it
            if (context.Body is string jsonString)
            {
                if (string.IsNullOrWhiteSpace(jsonString))
                    return CreateEmptyJsonObject();
                
                return JsonSerializer.Deserialize<JsonElement>(jsonString);
            }

            // For other types, serialize then deserialize to JsonElement
            var serialized = JsonSerializer.Serialize(context.Body);
            return JsonSerializer.Deserialize<JsonElement>(serialized);
        }
        catch (JsonException)
        {
            // If JSON parsing fails, return empty JsonObject
            return CreateEmptyJsonObject();
        }
        catch (Exception)
        {
            // For any other exceptions, return empty JsonObject
            return CreateEmptyJsonObject();
        }
    }

    /// <summary>
    /// Safely converts the script context headers to Dictionary for use in remote instance creation.
    /// </summary>
    /// <param name="context">The script context.</param>
    /// <returns>A Dictionary representation of the headers, or empty dictionary if conversion fails.</returns>
    public static Dictionary<string, string> GetHeadersAsDictionary(this ScriptContext context)
        => ToHeaderDictionary(context.Headers);

    /// <summary>
    /// The request headers as they arrived — not the script view, which omits the <c>Workflow:HeaderExposure:Deny</c>
    /// names. For the runtime's own reads only: the instance-call credential it forwards and the identity it propagates.
    /// </summary>
    internal static Dictionary<string, string> GetTransportHeadersAsDictionary(this ScriptContext context)
        => ToHeaderDictionary((object?)context.TransportHeaders ?? (object?)context.Headers);

    private static Dictionary<string, string> ToHeaderDictionary(object? headers)
    {
        try
        {
            if (headers == null)
                return new Dictionary<string, string>();

            // If Headers is already a Dictionary<string, string>, return it directly
            if (headers is Dictionary<string, string> headerDict)
                return headerDict;

            // If Headers is a string (JSON), parse it
            if (headers is string jsonString)
            {
                if (string.IsNullOrWhiteSpace(jsonString))
                    return new Dictionary<string, string>();
                
                return JsonSerializer.Deserialize<Dictionary<string, string>>(jsonString) 
                       ?? new Dictionary<string, string>();
            }

            // For other types, try to serialize then deserialize
            var serialized = JsonSerializer.Serialize(headers);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(serialized) 
                   ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            // If JSON parsing fails, return empty dictionary
            return new Dictionary<string, string>();
        }
        catch (Exception)
        {
            // For any other exceptions, return empty dictionary
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Safely converts the script context route values to Dictionary for use in remote instance creation.
    /// </summary>
    /// <param name="context">The script context.</param>
    /// <returns>A Dictionary representation of the route values, or empty dictionary if conversion fails.</returns>
    public static Dictionary<string, string> GetRouteValuesAsDictionary(this ScriptContext context)
    {
        try
        {
            if (context.RouteValues == null)
                return new Dictionary<string, string>();

            // If RouteValues is already a Dictionary<string, string>, return it directly
            if (context.RouteValues is Dictionary<string, string> routeDict)
                return routeDict;

            // If RouteValues is a string (JSON), parse it
            if (context.RouteValues is string jsonString)
            {
                if (string.IsNullOrWhiteSpace(jsonString))
                    return new Dictionary<string, string>();
                
                return JsonSerializer.Deserialize<Dictionary<string, string>>(jsonString) 
                       ?? new Dictionary<string, string>();
            }

            // For other types, try to serialize then deserialize
            var serialized = JsonSerializer.Serialize(context.RouteValues);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(serialized) 
                   ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            // If JSON parsing fails, return empty dictionary
            return new Dictionary<string, string>();
        }
        catch (Exception)
        {
            // For any other exceptions, return empty dictionary
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Creates a safe copy of ScriptContext with properly serialized properties for remote instance calls.
    /// </summary>
    /// <param name="context">The original script context.</param>
    /// <returns>A new ScriptContext with safely converted properties.</returns>
    public static (JsonElement Body, Dictionary<string, string> Headers, Dictionary<string, string> RouteValues) GetSafeProperties(this ScriptContext context)
    {
        return (
            Body: context.GetBodyAsJsonElement(),
            Headers: context.GetHeadersAsDictionary(),
            RouteValues: context.GetRouteValuesAsDictionary()
        );
    }

    /// <summary>
    /// Creates an empty JsonObject that can be safely serialized.
    /// </summary>
    /// <returns>A JsonElement representing an empty JSON object.</returns>
    private static JsonElement CreateEmptyJsonObject()
    {
        return JsonSerializer.Deserialize<JsonElement>("{}");
    }
} 