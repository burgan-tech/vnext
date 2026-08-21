using System.Text.Json;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Tasks.Executors.FanOut;

/// <summary>
/// Resolves a <c>FanOutTask</c>'s configured <c>itemsPath</c> against instance data into the
/// collection of <see cref="FanOutItem"/> the batch fans out over.
/// </summary>
/// <remarks>
/// <c>itemsPath</c> is a dot-path subset of JSONPath: <c>"$."</c> rooted, property navigation
/// only — no filters, wildcards, array indices or slices. <c>FanOutTask.Configure</c>
/// already rejects any path that does not start with <c>"$."</c>, so that prefix is assumed here.
/// </remarks>
public static class FanOutItemsResolver
{
    /// <summary>
    /// Walks <paramref name="itemsPath"/> against <paramref name="instanceData"/> and projects the
    /// resolved array into a list of <see cref="FanOutItem"/>.
    /// </summary>
    /// <param name="instanceData">
    /// The instance data to resolve the path against — the same raw <see cref="JsonElement"/> backing
    /// <c>Instance.Data</c> (see <c>InstanceData.Attributes</c> / <c>JsonData.JsonElement</c>).
    /// </param>
    /// <param name="itemsPath">The configured <c>"$."</c>-rooted dot-path, e.g. <c>"$.documents"</c>.</param>
    /// <returns>
    /// The resolved items, or an empty list when <paramref name="instanceData"/> is <c>null</c> or the
    /// path (or any intermediate segment) does not exist — a missing collection is a successful
    /// no-op batch, not an error.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The path resolves to a value that exists but is not a JSON array — this is a configuration
    /// error worth failing loudly rather than silently fanning out over nothing.
    /// </exception>
    public static IReadOnlyList<FanOutItem> Resolve(JsonElement? instanceData, string itemsPath)
    {
        if (instanceData is not { ValueKind: JsonValueKind.Object or JsonValueKind.Array } root)
        {
            return [];
        }

        var segments = itemsPath["$.".Length..].Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return [];
        }

        var current = root;
        foreach (var segment in segments)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out var next))
            {
                // Missing intermediate segment, or the path walks into a non-object (e.g. a string)
                // trying to navigate further — both are "the path does not resolve", not an error.
                return [];
            }

            current = next;
        }

        if (current.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"FanOutTask itemsPath '{itemsPath}' must resolve to a JSON array, but resolved to '{current.ValueKind}'.");
        }

        var items = new List<FanOutItem>();
        var index = 0;
        foreach (var element in current.EnumerateArray())
        {
            items.Add(new FanOutItem(index, element.ToDynamic(), ExtractItemKey(element, index)));
            index++;
        }

        return items;
    }

    /// <summary>
    /// Projects an already-materialised collection — the one a mapping's <c>ItemSelector</c>
    /// returned — into <see cref="FanOutItem"/>s. The alternative to <see cref="Resolve"/>: the
    /// two item sources differ only in where the values come from, so the index assignment and
    /// the stable-key rule are shared rather than duplicated per source.
    /// </summary>
    /// <param name="values">The selected item values, in the order the mapping produced them.</param>
    public static IReadOnlyList<FanOutItem> Project(IEnumerable<dynamic?> values)
    {
        var items = new List<FanOutItem>();
        var index = 0;
        foreach (var value in values)
        {
            items.Add(new FanOutItem(index, value, ExtractItemKey((object?)value, index)));
            index++;
        }

        return items;
    }

    /// <summary>
    /// Derives an item's stable key: its <c>id</c> string property if present, else its <c>key</c>
    /// string property, else its zero-based index as a string.
    /// </summary>
    private static string ExtractItemKey(JsonElement element, int index)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryReadString(element, "id", out var id))
            {
                return id;
            }

            if (TryReadString(element, "key", out var key))
            {
                return key;
            }
        }

        return index.ToString();
    }

    /// <summary>
    /// The same rule as the <see cref="JsonElement"/> overload, over a value that has already been
    /// converted to dynamic (an <c>ExpandoObject</c>, i.e. an <c>IDictionary&lt;string, object?&gt;</c>)
    /// or that is some other CLR object a mapping produced.
    /// </summary>
    private static string ExtractItemKey(object? value, int index) => value switch
    {
        null => index.ToString(),
        JsonElement element => ExtractItemKey(element, index),
        IDictionary<string, object?> map =>
            AsNonEmptyString(map, "id") ?? AsNonEmptyString(map, "key") ?? index.ToString(),
        // A mapping's ItemSelector commonly returns anonymous or typed objects rather than
        // dictionaries, so the same id/key rule is applied over their properties. Without this
        // the rule would silently degrade to "index" for the most natural thing a script returns.
        _ => ReadPropertyAsString(value, "id") ?? ReadPropertyAsString(value, "key") ?? index.ToString()
    };

    private static bool TryReadString(JsonElement element, string property, out string value)
    {
        if (element.TryGetProperty(property, out var el) &&
            el.ValueKind == JsonValueKind.String &&
            el.GetString() is { Length: > 0 } text)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string? AsNonEmptyString(IDictionary<string, object?> map, string property) =>
        map.TryGetValue(property, out var raw) && raw is string { Length: > 0 } text ? text : null;

    /// <summary>
    /// Reads a public instance property by name, case-insensitively. Enumerated rather than looked
    /// up with <c>BindingFlags.IgnoreCase</c> so a type declaring both <c>id</c> and <c>Id</c>
    /// picks the first match instead of throwing <see cref="System.Reflection.AmbiguousMatchException"/>.
    /// </summary>
    private static string? ReadPropertyAsString(object value, string property)
    {
        var propertyInfo = value.GetType()
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .FirstOrDefault(p =>
                p.GetIndexParameters().Length == 0 &&
                string.Equals(p.Name, property, StringComparison.OrdinalIgnoreCase));

        return propertyInfo?.GetValue(value) is string { Length: > 0 } text ? text : null;
    }
}
