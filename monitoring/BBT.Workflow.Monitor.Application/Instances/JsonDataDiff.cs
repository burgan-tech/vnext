using System.Text.Json;

namespace BBT.Workflow.Monitor.Instances;

/// <summary>Pure, dependency-free JSON object diff used by the instance data-diff endpoint.</summary>
public static class JsonDataDiff
{
    /// <summary>A field that was added or removed (path + value).</summary>
    public sealed record DiffField(string Path, string Value);

    /// <summary>A field whose value changed between two versions (path + old/new values).</summary>
    public sealed record DiffChange(string Path, string OldValue, string NewValue);

    /// <summary>The complete result of comparing two JSON elements.</summary>
    public sealed class DiffResult
    {
        /// <summary>Fields present in <c>to</c> but not in <c>from</c>.</summary>
        public List<DiffField> Added { get; } = [];

        /// <summary>Fields present in <c>from</c> but not in <c>to</c>.</summary>
        public List<DiffField> Removed { get; } = [];

        /// <summary>Fields present in both but with different values.</summary>
        public List<DiffChange> Changed { get; } = [];

        /// <summary>Number of leaf fields that are identical in both versions.</summary>
        public int UnchangedCount { get; set; }
    }

    /// <summary>
    /// Compares two JSON elements and returns the field-level diff.
    /// Nested objects are traversed recursively; paths use dot notation (e.g. <c>payment.amount</c>).
    /// Strings are unquoted in value representations; all other scalars use their raw JSON text.
    /// </summary>
    /// <param name="from">The baseline JSON element (older version).</param>
    /// <param name="to">The target JSON element (newer version).</param>
    /// <returns>A <see cref="DiffResult"/> describing all added, removed, changed, and unchanged fields.</returns>
    public static DiffResult Compare(JsonElement from, JsonElement to)
    {
        var result = new DiffResult();
        Walk(from, to, prefix: null, result);
        return result;
    }

    private static void Walk(JsonElement from, JsonElement to, string? prefix, DiffResult result)
    {
        if (from.ValueKind == JsonValueKind.Object && to.ValueKind == JsonValueKind.Object)
        {
            var fromProps = from.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
            var toProps = to.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

            foreach (var (name, fv) in fromProps)
            {
                var path = prefix is null ? name : $"{prefix}.{name}";
                if (!toProps.TryGetValue(name, out var tv))
                {
                    result.Removed.Add(new DiffField(path, Raw(fv)));
                    continue;
                }

                if (fv.ValueKind == JsonValueKind.Object && tv.ValueKind == JsonValueKind.Object)
                    Walk(fv, tv, path, result);
                else if (fv.GetRawText() == tv.GetRawText())
                    result.UnchangedCount++;
                else
                    result.Changed.Add(new DiffChange(path, Raw(fv), Raw(tv)));
            }

            foreach (var (name, tv) in toProps)
            {
                if (fromProps.ContainsKey(name)) continue;
                var path = prefix is null ? name : $"{prefix}.{name}";
                result.Added.Add(new DiffField(path, Raw(tv)));
            }
        }
        else if (from.GetRawText() == to.GetRawText())
        {
            result.UnchangedCount++;
        }
        else
        {
            result.Changed.Add(new DiffChange(prefix ?? "$", Raw(from), Raw(to)));
        }
    }

    // Strings are unquoted ("4" not "\"4\""); complex values keep their JSON text.
    private static string Raw(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText();
}
