using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Custom JSON converter for a transition's <c>availableIn</c> list that supports both the legacy
/// bare-string format (<c>"availableIn": ["review", "approval"]</c>) and the object format carrying
/// per-state role grants (<c>"availableIn": [{ "state": "approval", "roles": [ ... ] }]</c>).
/// The two forms may be mixed freely within one array.
/// </summary>
/// <remarks>
/// The written shape is derived from <see cref="AvailableInEntry.HasRoles"/>; it is not a record of how
/// the entry was authored. So the bare-string form and the roles-bearing object form round-trip
/// byte-for-byte, while a role-less <i>object</i> (<c>{ "state": "x" }</c> or
/// <c>{ "state": "x", "roles": [] }</c>) is normalized to the equivalent <c>"x"</c>. That is lossless —
/// both encode "available in x, no role narrowing" — and it is the same rule
/// <see cref="ViewDisplayJsonConverter"/> applies when it writes an SDI-only display back as a bare
/// string rather than <c>{ "sdi": … }</c>.
/// <para>
/// Deliberately no "authored as object" flag on <see cref="AvailableInEntry"/>: it would carry a
/// serialization detail into the domain model to buy byte-exactness on a shape that has no distinct
/// meaning, and would diverge from the converter this one is modelled on.
/// </para>
/// </remarks>
public sealed class AvailableInJsonConverter : JsonConverter<List<AvailableInEntry>>
{
    /// <summary>
    /// Opts into being called for a JSON null. Without this, System.Text.Json assigns null straight to
    /// the property for a reference type and never invokes <see cref="Read"/> — leaving
    /// <c>Transition.AvailableIn</c> null and making every <c>IsAvailableInState</c> call throw. The
    /// schema requires <c>"availableIn": null</c> for non-manual shared transitions, so null is normal
    /// input, not an edge case.
    /// </summary>
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override List<AvailableInEntry>? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        // An explicit null means "no restriction" — the same as an absent or empty array. The schema
        // requires null here for non-manual shared transitions, so this is a normal input.
        if (reader.TokenType == JsonTokenType.Null)
            return [];

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            // Unknown format — treat as absent instead of throwing, consistent with
            // ViewDisplayJsonConverter and ViewDefinitionJsonConverter.
            reader.Skip();
            return [];
        }

        var entries = new List<AvailableInEntry>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.EndArray:
                    return entries;

                case JsonTokenType.String:
                    var state = reader.GetString();
                    if (!string.IsNullOrWhiteSpace(state))
                        entries.Add(AvailableInEntry.FromState(state));
                    break;

                case JsonTokenType.StartObject:
                    var entry = JsonSerializer.Deserialize<AvailableInEntry>(ref reader, options);
                    if (entry != null)
                        entries.Add(entry);
                    break;

                default:
                    // Skip anything we do not recognise rather than failing the whole definition.
                    reader.Skip();
                    break;
            }
        }

        return entries;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, List<AvailableInEntry> value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();

        foreach (var entry in value)
        {
            // Role-less entries are written as the legacy bare-string shape, whether they were authored
            // that way or as a role-less object — the two are equivalent, so this is a normalization.
            if (!entry.HasRoles)
            {
                writer.WriteStringValue(entry.State);
                continue;
            }

            JsonSerializer.Serialize(writer, entry, options);
        }

        writer.WriteEndArray();
    }
}
