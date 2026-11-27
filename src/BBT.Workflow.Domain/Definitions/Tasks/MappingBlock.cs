using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// One declarative mapping block of a task binding (<c>input</c> or <c>output</c>): an optional
/// <see cref="Select"/> path, an optional <see cref="Into"/> path and an ordered <see cref="Set"/> of
/// path-to-expression assignments. Contract only; publish validation and execution live elsewhere.
/// </summary>
/// <param name="Select">JSON path selecting the value to read.</param>
/// <param name="Into">JSON path receiving the selected value.</param>
/// <param name="Set">Path-to-expression assignments; entries apply in declaration order.</param>
public sealed record MappingBlock(
    [property: JsonPropertyName("select")] string? Select,
    [property: JsonPropertyName("into")] string? Into,
    [property: JsonPropertyName("set")]
    [property: JsonConverter(typeof(OrderedStringMapJsonConverter))]
    IReadOnlyDictionary<string, string>? Set);

/// <summary>
/// Read-only string map whose enumeration order is the insertion order, guaranteed by a backing
/// array (a plain <see cref="Dictionary{TKey,TValue}"/> does not contractually keep order).
/// </summary>
public sealed class OrderedStringMap : IReadOnlyDictionary<string, string>
{
    private readonly KeyValuePair<string, string>[] _entries;
    private readonly Dictionary<string, string> _index;

    /// <summary>Builds the map from <paramref name="entries"/>; duplicate keys are rejected.</summary>
    public OrderedStringMap(IEnumerable<KeyValuePair<string, string>> entries)
    {
        _entries = entries.ToArray();
        _index = new Dictionary<string, string>(_entries.Length, StringComparer.Ordinal);
        foreach (var (key, value) in _entries)
        {
            if (!_index.TryAdd(key, value))
            {
                throw new ArgumentException($"Duplicate key '{key}'.", nameof(entries));
            }
        }
    }

    public int Count => _entries.Length;
    public IEnumerable<string> Keys => _entries.Select(e => e.Key);
    public IEnumerable<string> Values => _entries.Select(e => e.Value);
    public string this[string key] => _index[key];
    public bool ContainsKey(string key) => _index.ContainsKey(key);
    public bool TryGetValue(string key, out string value) => _index.TryGetValue(key, out value!);
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
        ((IEnumerable<KeyValuePair<string, string>>)_entries).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Reads a JSON object of string values into an <see cref="OrderedStringMap"/> (document order kept,
/// keys used verbatim, so the camel-case dictionary key policy never rewrites a path) and writes it back
/// in the same order.
/// </summary>
public sealed class OrderedStringMapJsonConverter : JsonConverter<IReadOnlyDictionary<string, string>>
{
    public override IReadOnlyDictionary<string, string>? Read( // skipcq: CS-R1138 — JsonConverter's signature
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected a JSON object of path to expression strings.");
        }

        var entries = new List<KeyValuePair<string, string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = reader.GetString()!;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"Expression for '{key}' must be a string.");
            }

            if (!seen.Add(key))
            {
                throw new JsonException($"Duplicate key '{key}'.");
            }

            entries.Add(new KeyValuePair<string, string>(key, reader.GetString()!));
        }

        return new OrderedStringMap(entries);
    }

    public override void Write(
        Utf8JsonWriter writer, IReadOnlyDictionary<string, string> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, expression) in value)
        {
            writer.WriteString(key, expression);
        }

        writer.WriteEndObject();
    }
}
