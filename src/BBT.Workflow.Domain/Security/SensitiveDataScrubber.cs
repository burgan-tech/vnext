using System.Text.Json;
using BBT.Workflow.Definitions.Schemas;

namespace BBT.Workflow.Security;

/// <summary>
/// Removes an instance's sensitive values from text on its way to a log sink or a diagnostic
/// message, replacing each with its <c>x-sensitive.maskingPattern</c>.
/// <para>
/// Scrubbing is <b>value-based</b>, not path-based, and that is the whole point. The leak vectors
/// hand us a bare value with no path attached — a script writing
/// <c>LogInformation("{e}", context.Instance.Data.email)</c>, or a JSON Schema validation message
/// quoting the offending value back at us. Knowing that <c>email</c> is sensitive does not help
/// there; knowing <i>what the email is</i> does. So the scrubber is built per instance from the
/// data it is about to protect, and then matches on the values themselves.
/// </para>
/// <para>
/// Consequently a scrubber is only ever valid for the instance it was built from, and it must be
/// rebuilt when the data changes. It is immutable and safe to share for that instance's lifetime.
/// </para>
/// </summary>
public sealed class SensitiveDataScrubber
{
    /// <summary>
    /// Values shorter than this are not scrubbed. A one- or two-character value (a status flag, a
    /// digit) occurs incidentally all over a log line, and replacing every occurrence would
    /// shred the message while protecting almost nothing. Short sensitive values are therefore
    /// protected by encryption and field-level roles, not by log scrubbing.
    /// </summary>
    public const int MinScrubbableLength = 3;

    /// <summary>A scrubber with nothing to scrub. Every operation is an identity.</summary>
    public static readonly SensitiveDataScrubber None = new([]);

    /// <summary>
    /// Ordered longest-value-first, so a value that contains another value is replaced before its
    /// own substring is, and the mask of the longer value survives.
    /// </summary>
    private readonly (string Value, string Mask)[] _replacements;

    private SensitiveDataScrubber((string Value, string Mask)[] replacements)
        => _replacements = replacements;

    /// <summary>True when this scrubber found nothing to protect — callers can skip it entirely.</summary>
    public bool IsEmpty => _replacements.Length == 0;

    /// <summary>
    /// Builds a scrubber for one instance's data from its master schema's <c>x-sensitive</c>
    /// annotations. Only fields with <c>redactInLogs</c> contribute.
    /// </summary>
    /// <param name="data">The instance data to collect sensitive values from.</param>
    /// <param name="sensitiveFields">Path → metadata, from <see cref="SensitiveSchemaParser.Parse"/>.</param>
    /// <returns><see cref="None"/> when nothing is annotated or no annotated field holds a value.</returns>
    public static SensitiveDataScrubber Create(
        JsonElement? data,
        IReadOnlyDictionary<string, SensitiveFieldMetadata> sensitiveFields)
    {
        if (data is null || sensitiveFields.Count == 0)
            return None;

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (path, metadata) in sensitiveFields)
        {
            if (!metadata.RedactInLogs)
                continue;

            foreach (var value in CollectValues(data.Value, path))
            {
                if (value.Length >= MinScrubbableLength)
                    replacements[value] = SensitiveValueMasker.Mask(value, metadata.MaskingPattern);
            }
        }

        if (replacements.Count == 0)
            return None;

        var ordered = replacements
            .OrderByDescending(pair => pair.Key.Length)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (pair.Key, pair.Value))
            .ToArray();

        return new SensitiveDataScrubber(ordered);
    }

    /// <summary>
    /// Replaces every occurrence of a known sensitive value in <paramref name="text"/> with its
    /// mask. Returns the input unchanged when there is nothing to replace.
    /// </summary>
    /// <param name="text">Arbitrary text — a log message, an exception message, a JSON fragment.</param>
    /// <returns>The scrubbed text; null in, null out.</returns>
    public string? Scrub(string? text)
    {
        if (IsEmpty || string.IsNullOrEmpty(text))
            return text;

        var result = text;
        foreach (var (value, mask) in _replacements)
        {
            result = result.Replace(value, mask, StringComparison.Ordinal);
        }

        return result;
    }

    /// <summary>
    /// Scrubs one structured-logging argument.
    /// <para>
    /// Non-string arguments keep their type unless scrubbing actually changed something — an
    /// <c>int</c> or a <c>Guid</c> is passed straight through so structured logging still sees a
    /// number, not a string. Only when a value's text form genuinely contains a sensitive value
    /// (a <c>JsonElement</c> holding the whole data object being the case that matters) does the
    /// argument degrade to a scrubbed string.
    /// </para>
    /// </summary>
    /// <param name="argument">The argument as handed to the logger.</param>
    /// <returns>The argument, scrubbed if it needed it.</returns>
    public object? ScrubArgument(object? argument)
    {
        if (IsEmpty || argument is null)
            return argument;

        if (argument is string text)
            return Scrub(text);

        var rendered = argument switch
        {
            JsonElement element => element.GetRawText(),
            _ => argument.ToString()
        };

        if (string.IsNullOrEmpty(rendered))
            return argument;

        var scrubbed = Scrub(rendered);
        return string.Equals(scrubbed, rendered, StringComparison.Ordinal) ? argument : scrubbed;
    }

    /// <summary>
    /// Scrubs a whole argument array, allocating only when at least one argument changed.
    /// </summary>
    /// <param name="arguments">The structured-logging arguments; may be null or empty.</param>
    /// <returns>The same array instance when nothing changed, otherwise a scrubbed copy.</returns>
    public object?[]? ScrubArguments(object?[]? arguments)
    {
        if (IsEmpty || arguments is null || arguments.Length == 0)
            return arguments;

        object?[]? scrubbed = null;

        for (var i = 0; i < arguments.Length; i++)
        {
            var value = ScrubArgument(arguments[i]);
            if (ReferenceEquals(value, arguments[i]))
                continue;

            scrubbed ??= (object?[])arguments.Clone();
            scrubbed[i] = value;
        }

        return scrubbed ?? arguments;
    }

    /// <summary>
    /// Resolves a schema path against instance data, yielding the text of every scalar it reaches.
    /// A <c>[]</c> segment fans out over an array, so <c>cards[].number</c> collects every card's
    /// number. Missing or structurally mismatched paths simply yield nothing.
    /// </summary>
    private static IEnumerable<string> CollectValues(JsonElement data, string path)
    {
        var current = new List<JsonElement> { data };

        foreach (var rawSegment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment;
            var arrayDepth = 0;
            while (segment.EndsWith("[]", StringComparison.Ordinal))
            {
                arrayDepth++;
                segment = segment[..^2];
            }

            var next = new List<JsonElement>();

            foreach (var node in current)
            {
                if (segment.Length == 0)
                {
                    next.Add(node);
                    continue;
                }

                if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(segment, out var child))
                    next.Add(child);
            }

            for (var depth = 0; depth < arrayDepth; depth++)
            {
                var unwrapped = new List<JsonElement>();
                foreach (var node in next)
                {
                    if (node.ValueKind != JsonValueKind.Array)
                        continue;

                    unwrapped.AddRange(node.EnumerateArray());
                }

                next = unwrapped;
            }

            current = next;
            if (current.Count == 0)
                return [];
        }

        var values = new List<string>();
        foreach (var node in current)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.String:
                    var text = node.GetString();
                    if (!string.IsNullOrEmpty(text))
                        values.Add(text);
                    break;

                // A card or account number is often authored as a number. Its raw text is what
                // would land in a log line, so protect that form too.
                case JsonValueKind.Number:
                    values.Add(node.GetRawText());
                    break;
            }
        }

        return values;
    }
}
