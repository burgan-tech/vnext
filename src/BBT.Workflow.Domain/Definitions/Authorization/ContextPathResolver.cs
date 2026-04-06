using System.Text.Json;

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

        var segments = SplitPath(path);
        if (segments.Length == 0)
            return [];

        var results = new List<string>();
        Walk(root, segments, 0, results);
        return results;
    }

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

    private static void Walk(JsonElement element, string[] segments, int index, List<string> results)
    {
        if (index >= segments.Length)
        {
            // Leaf: collect value
            var value = GetStringValue(element);
            if (value != null)
                results.Add(value);
            return;
        }

        var segment = segments[index];

        if (segment == ArrayWildcard)
        {
            // Expand array: iterate all elements and continue
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    Walk(item, segments, index + 1, results);
            }
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            // Case-insensitive property lookup
            if (TryGetPropertyIgnoreCase(element, segment, out var child))
                Walk(child, segments, index + 1, results);
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
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
