using System.Text.Json;

namespace BBT.Workflow.Headers;

/// <summary>
/// The request header names a domain keeps out of every place the runtime would otherwise copy them to: the recorded
/// transition history and every script/mapping context. Configured as <c>Workflow:HeaderExposure:Deny</c>; an empty list
/// (the default) changes nothing.
/// <para>
/// The list governs EXPOSURE only. The runtime's own reads of the request — caller-role resolution, the current user,
/// the instance-call credential it forwards, parent/root instance ids, telemetry — keep seeing the headers as they
/// arrived, so a name listed here can never break authorization or identity.
/// </para>
/// </summary>
public interface IHeaderExposureDenyList
{
    /// <summary>True when no header name is listed; callers then use the headers as they are.</summary>
    bool IsEmpty { get; }

    /// <summary>True when <paramref name="name"/> is listed (exact name, case-insensitive).</summary>
    bool IsDenied(string name);

    /// <summary>
    /// A case-insensitive copy of <paramref name="headers"/> without the listed names. Always a new dictionary, also
    /// when the list is empty, so the caller may hand it out without sharing the source.
    /// </summary>
    Dictionary<string, string?> Filter(IEnumerable<KeyValuePair<string, string?>> headers);

    /// <summary>
    /// <paramref name="recorded"/> without the listed names, as a new JSON object. With an empty list, or a
    /// <c>null</c>/undefined value, the value is returned as it is; a non-object value under a non-empty list is withheld
    /// (<c>null</c>), since its names cannot be checked.
    /// </summary>
    JsonElement? Apply(JsonElement? recorded);
}
