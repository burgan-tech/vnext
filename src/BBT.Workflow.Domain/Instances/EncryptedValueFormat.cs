using System.Text.Json;

namespace BBT.Workflow.Instances;

/// <summary>
/// The stored forms written by <c>x-encryption</c>:
/// <list type="bullet">
/// <item><c>encrypt</c> → <c>ENCRYPTED:AES256:&lt;keyVersion&gt;:&lt;base64url(version ‖ nonce ‖ ciphertext ‖ tag)&gt;</c>
/// (opened on load for the engine);</item>
/// <item><c>hash</c> → <c>HASHED:SHA256:&lt;hex&gt;</c> / <c>HASHED:SHA512:&lt;hex&gt;</c> (irreversible, the engine sees it too).</item>
/// </list>
/// Both prefixes are reserved: a request may not introduce a string carrying one of them, except by sending back the very
/// value already stored at the same path (a client that read the form and posts it back unchanged).
/// </summary>
public static class EncryptedValueFormat
{
    /// <summary>Reserved prefix of every stored <c>encrypt</c> token.</summary>
    public const string Prefix = "ENCRYPTED:AES256:";

    /// <summary>Reserved prefix of every stored <c>hash</c> digest.</summary>
    public const string HashPrefix = "HASHED:";

    /// <summary>Digest prefix for SHA-256.</summary>
    public const string Sha256HashPrefix = HashPrefix + "SHA256:";

    /// <summary>Digest prefix for SHA-512.</summary>
    public const string Sha512HashPrefix = HashPrefix + "SHA512:";

    // As they appear inside serialized JSON: a string value that starts with the prefix.
    private const string QuotedPrefix = "\"" + Prefix;
    private const string QuotedHashPrefix = "\"" + HashPrefix;

    /// <summary>True when <paramref name="value"/> is shaped like a stored <c>encrypt</c> token.</summary>
    public static bool IsToken(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>True when <paramref name="value"/> is shaped like a stored <c>hash</c> digest.</summary>
    public static bool IsHashed(string? value) =>
        value is not null && value.StartsWith(HashPrefix, StringComparison.Ordinal);

    /// <summary>True when <paramref name="value"/> carries one of the reserved prefixes.</summary>
    public static bool IsReserved(string? value) => IsToken(value) || IsHashed(value);

    /// <summary>
    /// Cheap pre-check over a serialized document: false means it certainly holds no token, so the caller
    /// can skip parsing it. True only means it might.
    /// </summary>
    public static bool MayContainToken(string? json) =>
        json is not null && json.Contains(QuotedPrefix, StringComparison.Ordinal);

    /// <summary>Cheap pre-check for either reserved prefix.</summary>
    public static bool MayContainReserved(string? json) =>
        json is not null && (json.Contains(QuotedPrefix, StringComparison.Ordinal) ||
                             json.Contains(QuotedHashPrefix, StringComparison.Ordinal));

    /// <summary>
    /// First dot path of <paramref name="payload"/> holding a string with a reserved prefix that is NOT the value
    /// already stored at that path in <paramref name="storedRoot"/> (the current row's stored form), or null. A value
    /// inside an array is always reported (stored forms never live in arrays) with a <c>[]</c> suffix.
    /// </summary>
    public static string? FindIntroducedToken(JsonElement payload, JsonElement? storedRoot)
    {
        return Walk(payload, string.Empty, insideArray: false);

        string? Walk(JsonElement node, string path, bool insideArray)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in node.EnumerateObject())
                    {
                        var childPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                        if (Walk(property.Value, childPath, insideArray) is { } hit)
                            return hit;
                    }
                    return null;

                case JsonValueKind.Array:
                    foreach (var item in node.EnumerateArray())
                        if (Walk(item, path, insideArray: true) is { } hit)
                            return hit;
                    return null;

                case JsonValueKind.String when IsReserved(node.GetString()):
                    if (insideArray)
                        return path + "[]";
                    return storedRoot is { } root &&
                           string.Equals(StringAt(root, path), node.GetString(), StringComparison.Ordinal)
                        ? null
                        : path;

                default:
                    return null;
            }
        }
    }

    /// <summary>String value at a dot path of <paramref name="root"/>, or null.</summary>
    public static string? StringAt(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
