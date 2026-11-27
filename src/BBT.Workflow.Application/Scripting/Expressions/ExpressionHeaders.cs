using System.Globalization;
using System.Text.Json;

namespace BBT.Workflow.Scripting.Expressions;

/// <summary>
/// The <c>headers</c> variable of an <see cref="ExpressionScope"/>: a read-only view over the incoming request's headers
/// — the very object behind <see cref="ScriptContext.Headers"/> (<c>context.Headers</c>), so whatever
/// <c>Workflow:HeaderExposure:Deny</c> removed from that view is absent here too. Never the runtime's transport headers.
/// Nothing is copied or serialized: every read looks the name up in the source on access.
/// Member names are lowercase because expressions spell them that way: <c>headers["x-channel"]</c> (the value, or null
/// when absent) and <c>headers.has("x-channel")</c>. Names match case-insensitively. A hyphenated name needs the
/// indexer — <c>headers.x-channel</c> parses as a subtraction.
/// </summary>
/// <remarks>
/// Lifecycle: built by <see cref="ExpressionScope"/> only when an expression names <c>headers</c>, at most once per scope.
/// The source is the dictionary the script context holds (a <c>Dictionary&lt;string, string?&gt;</c> in production);
/// other shapes the context may carry (an object-valued dictionary, an <c>ExpandoObject</c>, a JSON object) are read the
/// same way. A non-string value reads as its text.
/// </remarks>
#pragma warning disable IDE1006 // lowercase members are the expression-language surface
public sealed class ExpressionHeaders
{
    private readonly object? _source;

    internal ExpressionHeaders(object? source) => _source = source;

    /// <summary>The value of header <paramref name="name"/> (case-insensitive), or null when the request did not carry it.</summary>
    public string? this[string name] => TryGet(name, out var value) ? value : null;

    /// <summary>True when the request carried header <paramref name="name"/> (case-insensitive).</summary>
    public bool has(string name) => TryGet(name, out _);

    private bool TryGet(string? name, out string? value)
    {
        value = null;
        if (string.IsNullOrEmpty(name))
            return false;

        switch (_source)
        {
            case null:
                return false;
            case IDictionary<string, string?> strings:
                return TryFind(strings, name, out value);
            case IReadOnlyDictionary<string, string?> readOnlyStrings:
                return TryFind(readOnlyStrings, name, out value);
            case IDictionary<string, object?> objects:
                return TryFindObject(objects, name, out value);
            case IReadOnlyDictionary<string, object?> readOnlyObjects:
                return TryFindObject(readOnlyObjects, name, out value);
            case JsonElement { ValueKind: JsonValueKind.Object } element:
                foreach (var property in element.EnumerateObject())
                {
                    if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    value = TextOf(property.Value);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private static bool TryFind(IEnumerable<KeyValuePair<string, string?>> source, string name, out string? value)
    {
        // Exact (or the dictionary's own comparer) first: production header dictionaries are case-insensitive already.
        if (source is IDictionary<string, string?> map && map.TryGetValue(name, out value))
            return true;
        if (source is IReadOnlyDictionary<string, string?> readOnly && readOnly.TryGetValue(name, out value))
            return true;
        if (source is Dictionary<string, string?> concrete && ReferenceEquals(concrete.Comparer, StringComparer.OrdinalIgnoreCase))
        {
            value = null;
            return false;
        }

        foreach (var (key, candidate) in source)
        {
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                continue;
            value = candidate;
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryFindObject(IEnumerable<KeyValuePair<string, object?>> source, string name, out string? value)
    {
        object? raw = null;
        var found = source switch
        {
            IDictionary<string, object?> map => map.TryGetValue(name, out raw),
            IReadOnlyDictionary<string, object?> readOnly => readOnly.TryGetValue(name, out raw),
            _ => false
        };

        if (!found)
        {
            foreach (var (key, candidate) in source)
            {
                if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                raw = candidate;
                found = true;
                break;
            }
        }

        value = found ? TextOf(raw) : null;
        return found;
    }

    private static string? TextOf(object? value) => value switch
    {
        null => null,
        string text => text,
        JsonElement element => TextOf(element),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    private static string? TextOf(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.GetRawText()
    };
}
#pragma warning restore IDE1006
