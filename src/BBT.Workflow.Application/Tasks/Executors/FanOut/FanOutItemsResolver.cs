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
    /// Derives an item's stable key: its <c>id</c> string property if present, else its <c>key</c>
    /// string property, else its zero-based index as a string.
    /// </summary>
    private static string ExtractItemKey(JsonElement element, int index)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
            {
                return idEl.GetString()!;
            }

            if (element.TryGetProperty("key", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
            {
                return keyEl.GetString()!;
            }
        }

        return index.ToString();
    }
}
