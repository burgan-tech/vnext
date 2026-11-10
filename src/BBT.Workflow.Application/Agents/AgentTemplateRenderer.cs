using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BBT.Workflow.Agents;

/// <summary>
/// Renders <c>{{ path }}</c> placeholders of an agent prompt template or a memory key (issue #1105).
/// Roots: <c>data.*</c> (the instance's latest data), <c>body.*</c> (the transition payload),
/// <c>instance.id</c>, <c>instance.key</c>, <c>state</c>. Strings render as-is, other values as JSON,
/// missing paths as empty. The template itself is never evaluated as code.
/// </summary>
public static partial class AgentTemplateRenderer
{
    /// <summary>Values the template can read.</summary>
    /// <param name="Data">Latest instance data.</param>
    /// <param name="Body">Transition payload.</param>
    /// <param name="InstanceId">Instance id.</param>
    /// <param name="InstanceKey">Instance key.</param>
    /// <param name="State">The agent state.</param>
    public sealed record Scope(JsonElement? Data, JsonElement? Body, Guid InstanceId, string? InstanceKey, string State);

    /// <summary>Renders <paramref name="template"/>.</summary>
    public static string Render(string template, Scope scope) =>
        Placeholder().Replace(template, match => Resolve(match.Groups[1].Value.Trim(), scope) ?? string.Empty);

    /// <summary>
    /// Renders <c>prompt.context</c>: one <c>&lt;data name="…"&gt;</c> block per entry, in name order, holding the
    /// resolved value (JSON for objects and arrays). A missing path renders an empty block, never an error.
    /// </summary>
    public static string RenderContext(IReadOnlyDictionary<string, string>? context, Scope scope)
    {
        if (context is null || context.Count == 0)
            return string.Empty;

        var builder = new System.Text.StringBuilder();
        foreach (var (name, path) in context.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var value = (Resolve(path.Trim(), scope) ?? string.Empty)
                .Replace("</data", "<\\/data", StringComparison.OrdinalIgnoreCase);
            builder.Append("\n\n<data name=\"").Append(name).Append("\">\n").Append(value).Append("\n</data>");
        }

        return builder.ToString();
    }

    /// <summary>Resolves one path; null when it does not exist.</summary>
    public static string? Resolve(string path, Scope scope)
    {
        var segments = path.Split('.', 2);
        var root = segments[0];
        var rest = segments.Length > 1 ? segments[1] : null;

        switch (root)
        {
            case "state":
                return scope.State;
            case "instance":
                return rest switch
                {
                    "id" => scope.InstanceId.ToString(),
                    "key" => scope.InstanceKey,
                    _ => null
                };
            case "data":
                return Render(Navigate(scope.Data, rest));
            case "body":
                return Render(Navigate(scope.Body, rest));
            default:
                return null;
        }
    }

    /// <summary>Walks a dotted path with optional <c>[n]</c> indexes.</summary>
    public static JsonElement? Navigate(JsonElement? root, string? path)
    {
        if (root is not { } current)
            return null;
        if (string.IsNullOrEmpty(path))
            return current;

        foreach (var raw in path.Split('.'))
        {
            var name = raw;
            var indexes = new List<int>();
            var bracket = raw.IndexOf('[');
            if (bracket >= 0)
            {
                name = raw[..bracket];
                foreach (Match m in Index().Matches(raw[bracket..]))
                    indexes.Add(int.Parse(m.Groups[1].Value));
            }

            if (name.Length > 0)
            {
                if (current.ValueKind != JsonValueKind.Object || !TryGetCaseInsensitive(current, name, out current))
                    return null;
            }

            foreach (var i in indexes)
            {
                if (current.ValueKind != JsonValueKind.Array || i >= current.GetArrayLength())
                    return null;
                current = current[i];
            }
        }

        return current;
    }

    private static bool TryGetCaseInsensitive(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
            return true;
        foreach (var p in element.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? Render(JsonElement? value) => value switch
    {
        null => null,
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        { } other => other.GetRawText()
    };

    /// <summary>SHA-256 hex of a value.</summary>
    public static string Sha256(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [GeneratedRegex(@"\{\{\s*([^{}]+?)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\[(\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex Index();
}
