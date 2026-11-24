using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BBT.Workflow.Shared.Json;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Resolves a dot-notation path with optional array wildcard (<c>[*]</c>) against a <see cref="JsonElement"/> root.
/// Used by the dynamic role evaluation to extract values from the authorization context.
/// <para>
/// Path examples:
/// <list type="bullet">
///   <item><c>Instance.Data.customer.ownerUserId</c></item>
///   <item><c>Instance.Data.assignedUsers[*].userId</c></item>
///   <item><c>Transition.Key</c></item>
/// </list>
/// </para>
/// </summary>
public static class ContextPathResolver
{
    private const string ArrayWildcard = "[*]";

    // Paths come from component definitions (a finite set), so a static cache keyed by the path text is
    // fine; the cap only guards against a caller feeding unbounded distinct strings.
    private const int MaxCachedPaths = 4096;

    private static readonly ConcurrentDictionary<string, Parsed> ParseCache = new(StringComparer.Ordinal);

    // Keyed by the array instance SplitSegments hands out, so the pre-split overload reuses the compiled path.
    private static readonly ConditionalWeakTable<string[], JsonPath> PathBySegments = new();

    private sealed record Parsed(string[] Segments, JsonPath Path);

    /// <summary>
    /// Resolves a path against the given <paramref name="root"/> and returns all matched string values.
    /// Returns an empty list when the path does not exist or any segment is missing.
    /// Never throws on missing properties — all navigation errors yield an empty result.
    /// Property name matching is case-insensitive.
    /// </summary>
    /// <param name="root">The JSON root element representing the authorization context.</param>
    /// <param name="path">Dot-notation path, e.g. <c>Instance.Data.assignedUsers[*].userId</c></param>
    public static IReadOnlyList<string> Resolve(JsonElement root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return [];

        var parsed = GetParsed(path);
        return parsed.Segments.Length == 0 ? [] : Collect(root, parsed.Path, 0);
    }

    /// <summary>
    /// Resolves already-split <paramref name="segments"/> (from <see cref="SplitSegments"/>), starting at
    /// <paramref name="startIndex"/>, against <paramref name="root"/>. Exactly the walk <see cref="Resolve(JsonElement, string)"/>
    /// performs, so a caller that routes on the leading segments itself — the role grant evaluator, which
    /// resolves <c>Instance.Data.*</c> against the instance's own data element instead of a copy — gets the
    /// same values. A <paramref name="startIndex"/> equal to the segment count collects <paramref name="root"/>
    /// itself as the leaf.
    /// </summary>
    public static IReadOnlyList<string> Resolve(JsonElement root, string[] segments, int startIndex)
    {
        var path = PathBySegments.TryGetValue(segments, out var known)
            ? known
            : PathBySegments.GetValue(segments, JsonPath.FromSegments);
        return Collect(root, path, startIndex);
    }

    /// <summary>
    /// Splits a path into the segments <see cref="Resolve(JsonElement, string)"/> walks: dot-separated,
    /// trimmed, empty segments dropped, <c>[*]</c> as a segment of its own. Empty for a blank path. The
    /// returned array is cached per path text and shared — treat it as read-only.
    /// </summary>
    public static string[] SplitSegments(string path)
        => string.IsNullOrWhiteSpace(path) ? [] : GetParsed(path).Segments;

    /// <summary>
    /// Splits a path string into segments, normalizing <c>[*]</c> as a dedicated segment.
    /// Example: <c>"assignedUsers[*].userId"</c> → <c>["assignedUsers", "[*]", "userId"]</c>
    /// </summary>
    private static string[] SplitPath(string path)
    {
        // Replace [*] with a sentinel so we can split on dots
        var normalized = path.Replace(ArrayWildcard, ".[*]", StringComparison.Ordinal);

        return normalized
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToArray();
    }

    private static Parsed GetParsed(string path)
    {
        if (ParseCache.TryGetValue(path, out var cached))
            return cached;

        var segments = SplitPath(path);
        var parsed = new Parsed(segments, JsonPath.FromSegments(segments));
        if (ParseCache.Count < MaxCachedPaths)
            ParseCache.TryAdd(path, parsed);
        PathBySegments.AddOrUpdate(segments, parsed.Path);
        return parsed;
    }

    private static List<string> Collect(JsonElement root, JsonPath path, int startIndex)
    {
        var leaves = new List<JsonElement>();
        path.CollectLeaves(root, startIndex, ignoreCase: true, leaves);

        var results = new List<string>(leaves.Count);
        foreach (var leaf in leaves)
        {
            var value = GetStringValue(leaf);
            if (value != null)
                results.Add(value);
        }

        return results;
    }

    private static string? GetStringValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }
}
