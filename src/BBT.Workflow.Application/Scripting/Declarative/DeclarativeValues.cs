using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Shared.Json;

namespace BBT.Workflow.Scripting.Declarative;

/// <summary>
/// Turns what an expression returned into an owned <see cref="JsonNode"/> — never a view over a borrowed document — so
/// the value outlives the evaluation scope: a <see cref="RuleJsonDynamic"/> or <see cref="JsonElement"/> is copied from
/// its raw text, a <see cref="JsonNode"/> deep-cloned, anything else (strings, numbers, booleans, dates as the runtime's
/// normal ISO-8601 text, arrays) serialized with <see cref="JsonSerializerConstants.JsonOptions"/>. JSON null and
/// "missing" become null, which the runner skips.
/// </summary>
internal static class DeclarativeValues
{
    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        RuleJsonDynamic dynamicValue => ToNode(dynamicValue.AsJsonElement()),
        JsonElement element => element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : FromOwnedElement(CopySubtree(element)),
        JsonNode node => node.DeepClone(),
        string text => JsonValue.Create(text),
        _ => JsonSerializer.SerializeToNode(value, value.GetType(), JsonSerializerConstants.JsonOptions)
    };

    /// <summary>
    /// A subtree-sized, owned copy of <paramref name="element"/>: its raw UTF-8 bytes parsed into a new, non-pooled
    /// document. Unlike <see cref="JsonElement.Clone"/> — a no-op for an element of a non-disposable document, such as
    /// the <c>JsonElement</c> an HTTP or Dapr invoker returns — the copy never references the source document, so a
    /// small selected value does not pin a large task response for the life of the delta.
    /// </summary>
    private static JsonElement CopySubtree(JsonElement element)
    {
        var reader = new Utf8JsonReader(JsonMarshal.GetRawUtf8Value(element));
        return JsonElement.ParseValue(ref reader);
    }

    /// <summary>
    /// The node <c>JsonNode.Parse(element.GetRawText())</c> would build — an object, array or value over the element —
    /// without the text round trip, over an element <see cref="CopySubtree"/> already made independent of its source.
    /// </summary>
    private static JsonNode? FromOwnedElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => JsonObject.Create(element),
        JsonValueKind.Array => JsonArray.Create(element),
        _ => JsonValue.Create(element)
    };
}
