using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow;

/// <summary>
/// A <see cref="JsonConverter{T}"/> for <see cref="JsonElement"/> that tolerates an
/// <see cref="JsonValueKind.Undefined"/> element on write by emitting <c>null</c> instead of throwing.
/// <para>
/// The built-in converter calls <see cref="JsonElement.WriteTo"/>, which throws
/// <see cref="System.InvalidOperationException"/> for a <c>default(JsonElement)</c> (Undefined).
/// Domain entities expose optional <see cref="JsonElement"/> properties (e.g. a State Store task's
/// <c>Value</c>/<c>Query</c>/<c>Metadata</c>) that stay Undefined when the corresponding config field is
/// absent; serializing such an entity (component/artifact cache, instance-task request audit, …) would
/// otherwise fail. Reading is identical to the built-in behavior.
/// </para>
/// </summary>
public sealed class SafeJsonElementConverter : JsonConverter<JsonElement>
{
    public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.Clone();
    }

    public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            writer.WriteNullValue();
            return;
        }

        value.WriteTo(writer);
    }
}
