using System.Text.Json;

namespace BBT.Workflow;

/// <summary>
/// Shared JSON reading helpers for the rule-based contract definitions
/// (<see cref="ViewDefinition"/> and <see cref="SchemaDefinition"/>), whose converters accept the
/// same family of wire shapes: a bare component reference, a bare entry array, or a wrapped object.
/// </summary>
internal static class ContractDefinitionJsonReader
{
    /// <summary>
    /// True when the element is a component reference written inline (<c>{ "key": ..., "domain": ...,
    /// "flow": ..., "version": ... }</c>) rather than a wrapper object or an entry.
    /// A reference always carries <c>key</c>; wrapper objects and entries never do.
    /// </summary>
    public static bool IsBareReference(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty("key", out var key) &&
               key.ValueKind == JsonValueKind.String;
    }

    /// <summary>
    /// Deserializes a component reference from the given element, returning null when the element is
    /// not a usable reference. Never throws - an unreadable definition degrades to "not declared"
    /// rather than failing the whole component load.
    /// </summary>
    public static Reference? ReadReference(JsonElement element, JsonSerializerOptions options)
    {
        try
        {
            return JsonSerializer.Deserialize<Reference>(element.GetRawText(), options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
