using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// The <c>output</c> member of a workflow or function definition: either today's C# output script
/// (<see cref="Script"/>, implements <c>IOutputHandler</c>) or a declarative <see cref="Block"/> shaping
/// the document <c>{ data, statusCode, headers }</c>. Exactly one of the two is set.
/// </summary>
/// <param name="Script">The output script; <c>null</c> when the member is a block.</param>
/// <param name="Block">The declarative output block; <c>null</c> when the member is a script.</param>
[JsonConverter(typeof(OutputDefinitionJsonConverter))]
public sealed record OutputDefinition(ScriptCode? Script, MappingBlock? Block);

/// <summary>
/// Reads an <c>output</c> member by its shape: an object carrying <c>location</c> or <c>code</c> is a
/// script (read exactly as a <see cref="ScriptCode"/> is read today), an object carrying <c>select</c>,
/// <c>into</c> or <c>set</c> and neither of those is a <see cref="MappingBlock"/>, and an object carrying
/// both kinds of members is rejected with a <see cref="JsonException"/>. Writes back the member it holds,
/// so a script serializes to the same JSON as a bare <see cref="ScriptCode"/>.
/// </summary>
public sealed class OutputDefinitionJsonConverter : JsonConverter<OutputDefinition>
{
    private static readonly string[] ScriptMembers = ["location", "code"];
    private static readonly string[] BlockMembers = ["select", "into", "set"];

    /// <inheritdoc />
    public override OutputDefinition? Read( // skipcq: CS-R1138 — JsonConverter's signature
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("The 'output' member must be a JSON object (a script or a mapping block).");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var isScript = HasAny(root, ScriptMembers);
        var isBlock = HasAny(root, BlockMembers);

        if (isScript && isBlock)
        {
            throw new JsonException(
                "The 'output' member mixes a script ('location'/'code') with a mapping block ('select'/'into'/'set'); declare one of them.");
        }

        if (isBlock)
        {
            return new OutputDefinition(null, root.Deserialize<MappingBlock>(options));
        }

        // Script shape, and every other object, reads exactly as a ScriptCode did before the block existed.
        return new OutputDefinition(root.Deserialize<ScriptCode>(options), null);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, OutputDefinition value, JsonSerializerOptions options)
    {
        if (value.Block is not null)
        {
            JsonSerializer.Serialize(writer, value.Block, options);
            return;
        }

        if (value.Script is not null)
        {
            JsonSerializer.Serialize(writer, value.Script, options);
            return;
        }

        writer.WriteNullValue();
    }

    /// <summary>Case-insensitive member test, matching <see cref="ScriptCodeJsonConverter"/>'s lookup.</summary>
    private static bool HasAny(JsonElement root, string[] names)
    {
        foreach (var property in root.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
