using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Headers;

/// <summary>
/// Default <see cref="IHeaderExposureDenyList"/> over <see cref="HeaderExposureOptions.Deny"/>. Singleton: the list is
/// read once at construction; blank entries are ignored and names are trimmed.
/// </summary>
public sealed class HeaderExposureDenyList : IHeaderExposureDenyList
{
    private readonly HashSet<string> _deny;

    /// <summary>Creates the list from the bound options.</summary>
    public HeaderExposureDenyList(IOptions<HeaderExposureOptions> options)
    {
        _deny = new HashSet<string>(
            (options.Value.Deny ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool IsEmpty => _deny.Count == 0;

    /// <inheritdoc />
    public bool IsDenied(string name) => _deny.Count > 0 && name is not null && _deny.Contains(name);

    /// <inheritdoc />
    public Dictionary<string, string?> Filter(IEnumerable<KeyValuePair<string, string?>> headers)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            if (!IsDenied(name))
                result[name] = value;
        }

        return result;
    }

    /// <inheritdoc />
    public JsonElement? Apply(JsonElement? recorded)
    {
        if (_deny.Count == 0)
            return recorded;

        if (recorded is not { } element || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return recorded;

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var header in element.EnumerateObject().Where(h => !_deny.Contains(h.Name)))
                header.WriteTo(writer);

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
}
