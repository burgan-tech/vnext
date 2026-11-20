using System.Text.Json;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Instances.History;

/// <summary>Reduces a recorded transition header set to what transition history may return.</summary>
public interface ITransitionHistoryHeaderPolicy
{
    /// <summary>
    /// Returns the headers of <paramref name="recorded"/> that the configured lists let through, as a new JSON object.
    /// With no lists configured the recorded value is returned as it is. A configured policy withholds (returns
    /// <c>null</c>) a recorded value that is not a JSON object, since its names cannot be checked.
    /// </summary>
    JsonElement? Apply(JsonElement? recorded);
}

/// <summary>
/// Default <see cref="ITransitionHistoryHeaderPolicy"/> over <see cref="TransitionHistoryHeaderOptions.Read"/>.
/// Singleton: the lists are read once at construction.
/// </summary>
public sealed class TransitionHistoryHeaderPolicy : ITransitionHistoryHeaderPolicy
{
    private readonly HashSet<string> _allow;
    private readonly HashSet<string> _deny;

    /// <summary>Creates the policy from the bound options.</summary>
    public TransitionHistoryHeaderPolicy(IOptions<TransitionHistoryHeaderOptions> options)
    {
        var read = options.Value.Read ?? new TransitionHistoryHeaderLists();
        _allow = ToNameSet(read.Allow);
        _deny = ToNameSet(read.Deny);
    }

    /// <inheritdoc />
    public JsonElement? Apply(JsonElement? recorded)
    {
        if (_allow.Count == 0 && _deny.Count == 0)
            return recorded;

        if (recorded is not { } element || element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return recorded;

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var header in element.EnumerateObject())
            {
                if (_deny.Contains(header.Name) || (_allow.Count > 0 && !_allow.Contains(header.Name)))
                    continue;

                header.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static HashSet<string> ToNameSet(IEnumerable<string>? names) => new(
        (names ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()),
        StringComparer.OrdinalIgnoreCase);
}
