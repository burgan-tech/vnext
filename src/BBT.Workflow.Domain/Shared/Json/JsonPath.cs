using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Aether.Results;

namespace BBT.Workflow.Shared.Json;

/// <summary>
/// A compiled JSON path over a deliberately small subset of JSONPath: <c>$</c>, <c>.name</c>,
/// <c>['name']</c>, <c>[n]</c> (read only) and <c>[*]</c> (read only). Filters (<c>?()</c>), recursive
/// descent (<c>..</c>), negative indexes and slices are rejected at parse time.
/// <para>
/// A dot-name segment accepts any character except <c>.</c>, <c>[</c>, <c>]</c>, whitespace and quotes
/// (so <c>$.headers.x-channel</c> works). A bracket-quoted name accepts anything, including dots; inside it
/// <c>\'</c> and <c>\\</c> are escapes. Empty segments and a missing leading <c>$</c> are parse errors.
/// </para>
/// <para>
/// Instances are immutable and thread-safe; parse once and reuse. Reads are case-sensitive; the role-grant
/// resolver's case-insensitive walk lives in <see cref="BBT.Workflow.Definitions.ContextPathResolver"/>,
/// which parses through this type.
/// </para>
/// </summary>
public sealed class JsonPath
{
    private enum SegmentKind : byte { Name, Index, Wildcard }

    private readonly struct Segment
    {
        public Segment(SegmentKind kind, string? name, int index)
        {
            Kind = kind;
            Name = name;
            Index = index;
        }

        public SegmentKind Kind { get; }
        public string? Name { get; }
        public int Index { get; }
    }

    private const string ErrorCode = "JsonPath:Invalid";

    private readonly Segment[] _segments;

    private JsonPath(string text, Segment[] segments)
    {
        Text = text;
        _segments = segments;
        var writable = true;
        foreach (var s in segments)
        {
            if (s.Kind != SegmentKind.Name)
            {
                writable = false;
                break;
            }
        }

        IsWritable = writable;
        FirstSegment = segments.Length > 0 && segments[0].Kind == SegmentKind.Name ? segments[0].Name! : string.Empty;
    }

    /// <summary>The path text this instance was parsed from.</summary>
    public string Text { get; }

    /// <summary>
    /// True when the path has no index and no wildcard segment, so <see cref="Set"/> can address it. The root
    /// path <c>$</c> is "writable" by this definition but <see cref="Set"/> still refuses it.
    /// </summary>
    public bool IsWritable { get; }

    /// <summary>
    /// The first segment's name — the variable name for input-phase sources. Empty for <c>$</c> or when the
    /// first segment is an index or wildcard.
    /// </summary>
    public string FirstSegment { get; }

    /// <summary>Parses <paramref name="text"/>; a failure carries a validation error naming the offending position.</summary>
    public static Result<JsonPath> Parse(string text)
    {
        if (text is null)
            return Fail("A JSON path is required.", text);
        if (text.Length == 0 || text[0] != '$')
            return Fail("A JSON path must start with '$'.", text);

        var segments = new List<Segment>();
        var i = 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '.')
            {
                i++;
                var start = i;
                while (i < text.Length && IsNameChar(text[i]))
                    i++;
                if (i == start)
                    return Fail($"Empty or invalid segment after '.' at position {start}.", text);
                segments.Add(new Segment(SegmentKind.Name, text.Substring(start, i - start), 0));
            }
            else if (c == '[')
            {
                i++;
                if (i >= text.Length)
                    return Fail("Unterminated '['.", text);

                if (text[i] == '\'')
                {
                    i++;
                    var sb = new StringBuilder();
                    var closed = false;
                    while (i < text.Length)
                    {
                        var ch = text[i];
                        if (ch == '\\' && i + 1 < text.Length && (text[i + 1] == '\'' || text[i + 1] == '\\'))
                        {
                            sb.Append(text[i + 1]);
                            i += 2;
                            continue;
                        }

                        if (ch == '\'')
                        {
                            closed = true;
                            i++;
                            break;
                        }

                        sb.Append(ch);
                        i++;
                    }

                    if (!closed)
                        return Fail("Unterminated quoted name.", text);
                    if (i >= text.Length || text[i] != ']')
                        return Fail($"Expected ']' at position {i}.", text);
                    i++;
                    segments.Add(new Segment(SegmentKind.Name, sb.ToString(), 0));
                }
                else if (text[i] == '*')
                {
                    i++;
                    if (i >= text.Length || text[i] != ']')
                        return Fail($"Expected ']' at position {i}.", text);
                    i++;
                    segments.Add(new Segment(SegmentKind.Wildcard, null, 0));
                }
                else
                {
                    var start = i;
                    while (i < text.Length && text[i] is >= '0' and <= '9')
                        i++;
                    if (i == start || i >= text.Length || text[i] != ']'
                        || !int.TryParse(text.AsSpan(start, i - start), out var index))
                        return Fail($"Unsupported bracket expression at position {start - 1}; only ['name'], [n] and [*] are allowed.", text);
                    i++;
                    segments.Add(new Segment(SegmentKind.Index, null, index));
                }
            }
            else
            {
                return Fail($"Unexpected character '{c}' at position {i}.", text);
            }
        }

        return Result<JsonPath>.Ok(new JsonPath(text, segments.ToArray()));
    }

    /// <summary>
    /// Builds a path from already-split plain segments: each is a property name, except the literal
    /// <c>[*]</c> which is a wildcard. No validation — this is the lenient legacy shape of
    /// <c>ContextPathResolver</c>.
    /// </summary>
    internal static JsonPath FromSegments(string[] segments)
    {
        var list = new Segment[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            list[i] = segments[i] == "[*]"
                ? new Segment(SegmentKind.Wildcard, null, 0)
                : new Segment(SegmentKind.Name, segments[i], 0);
        }

        return new JsonPath(string.Join('.', segments), list);
    }

    /// <summary>
    /// Reads the value at this path. Without a wildcard that is the addressed element; with one it is a JSON
    /// array of every match in document order (nested wildcards flatten). Returns false when the path does
    /// not resolve; a wildcard applied to a non-array yields false, an empty array yields an empty
    /// projection.
    /// </summary>
    public bool TryRead(JsonElement root, out JsonElement value)
    {
        if (!HasWildcard())
        {
            var current = root;
            foreach (var s in _segments)
            {
                if (!Step(current, s, ignoreCase: false, out current))
                {
                    value = default;
                    return false;
                }
            }

            value = current;
            return true;
        }

        var matches = new List<JsonElement>();
        var touchedArray = false;
        CollectWalk(root, 0, ignoreCase: false, matches, ref touchedArray);
        if (!touchedArray)
        {
            value = default;
            return false;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var m in matches)
                m.WriteTo(writer);
            writer.WriteEndArray();
        }

        value = JsonDocument.Parse(buffer.WrittenMemory).RootElement;
        return true;
    }

    /// <summary>
    /// <see cref="JsonNode"/> variant of <see cref="TryRead(JsonElement, out JsonElement)"/>. A JSON
    /// <c>null</c> at the path is found (true) with a null <paramref name="value"/>. Wildcard matches are
    /// deep-cloned into a fresh <see cref="JsonArray"/>; a non-wildcard result is the live node.
    /// </summary>
    public bool TryRead(JsonNode root, out JsonNode? value)
    {
        if (!HasWildcard())
        {
            JsonNode? current = root;
            foreach (var s in _segments)
            {
                if (!StepNode(current, s, out current))
                {
                    value = null;
                    return false;
                }
            }

            value = current;
            return true;
        }

        var result = new JsonArray();
        var touchedArray = false;
        CollectNodes(root, 0, result, ref touchedArray);
        if (!touchedArray)
        {
            value = null;
            return false;
        }

        value = result;
        return true;
    }

    /// <summary>
    /// Sets <paramref name="value"/> at this path, creating intermediate <see cref="JsonObject"/>s. An
    /// existing property is replaced (arrays whole). A value that already has a parent is deep-cloned first.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The path is not <see cref="IsWritable"/>, is the root <c>$</c>, or an intermediate exists and is not a
    /// JSON object. Publish validation rejects non-writable paths, so reaching this at run time is a bug.
    /// </exception>
    public void Set(JsonObject root, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!IsWritable)
            throw new InvalidOperationException($"JSON path '{Text}' is not writable (index or wildcard segment).");
        if (_segments.Length == 0)
            throw new InvalidOperationException("Setting the root path '$' is not supported.");

        var current = root;
        for (var i = 0; i < _segments.Length - 1; i++)
        {
            var name = _segments[i].Name!;
            if (current.TryGetPropertyValue(name, out var existing))
            {
                if (existing is not JsonObject obj)
                    throw new InvalidOperationException(
                        $"Cannot set '{Text}': '{name}' exists and is not a JSON object.");
                current = obj;
            }
            else
            {
                var created = new JsonObject();
                current[name] = created;
                current = created;
            }
        }

        current[_segments[^1].Name!] = value?.Parent is not null ? value.DeepClone() : value;
    }

    /// <summary>
    /// Case-sensitivity-selectable leaf walk used by <c>ContextPathResolver</c>: appends every leaf the path
    /// reaches from <paramref name="startIndex"/> (segments before it are skipped) in document order.
    /// </summary>
    internal void CollectLeaves(JsonElement root, int startIndex, bool ignoreCase, List<JsonElement> sink)
    {
        var touched = false;
        CollectWalk(root, startIndex, ignoreCase, sink, ref touched);
    }

    private bool HasWildcard()
    {
        foreach (var s in _segments)
        {
            if (s.Kind == SegmentKind.Wildcard)
                return true;
        }

        return false;
    }

    private void CollectWalk(JsonElement element, int index, bool ignoreCase, List<JsonElement> sink, ref bool touchedArray)
    {
        if (index >= _segments.Length)
        {
            sink.Add(element);
            return;
        }

        var segment = _segments[index];
        if (segment.Kind == SegmentKind.Wildcard)
        {
            if (element.ValueKind != JsonValueKind.Array)
                return;
            touchedArray = true;
            foreach (var item in element.EnumerateArray())
                CollectWalk(item, index + 1, ignoreCase, sink, ref touchedArray);
            return;
        }

        if (Step(element, segment, ignoreCase, out var child))
            CollectWalk(child, index + 1, ignoreCase, sink, ref touchedArray);
    }

    private void CollectNodes(JsonNode? node, int index, JsonArray sink, ref bool touchedArray)
    {
        if (index >= _segments.Length)
        {
            sink.Add(node?.DeepClone());
            return;
        }

        var segment = _segments[index];
        if (segment.Kind == SegmentKind.Wildcard)
        {
            if (node is not JsonArray array)
                return;
            touchedArray = true;
            foreach (var item in array)
                CollectNodes(item, index + 1, sink, ref touchedArray);
            return;
        }

        if (StepNode(node, segment, out var child))
            CollectNodes(child, index + 1, sink, ref touchedArray);
    }

    private static bool Step(JsonElement element, Segment segment, bool ignoreCase, out JsonElement child)
    {
        switch (segment.Kind)
        {
            case SegmentKind.Name when element.ValueKind == JsonValueKind.Object:
                if (!ignoreCase)
                    return element.TryGetProperty(segment.Name!, out child);
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, segment.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        child = property.Value;
                        return true;
                    }
                }

                break;
            case SegmentKind.Index when element.ValueKind == JsonValueKind.Array:
                if (segment.Index < element.GetArrayLength())
                {
                    child = element[segment.Index];
                    return true;
                }

                break;
        }

        child = default;
        return false;
    }

    private static bool StepNode(JsonNode? node, Segment segment, out JsonNode? child)
    {
        switch (segment.Kind)
        {
            case SegmentKind.Name when node is JsonObject obj:
                return obj.TryGetPropertyValue(segment.Name!, out child);
            case SegmentKind.Index when node is JsonArray array:
                if (segment.Index < array.Count)
                {
                    child = array[segment.Index];
                    return true;
                }

                break;
        }

        child = null;
        return false;
    }

    private static bool IsNameChar(char c)
        => !(c is '.' or '[' or ']' or '\'' or '"' || char.IsWhiteSpace(c));

    private static Result<JsonPath> Fail(string message, string? text)
        => Result<JsonPath>.Fail(Error.Validation(ErrorCode, message, text ?? string.Empty));

    /// <inheritdoc />
    public override string ToString() => Text;
}
