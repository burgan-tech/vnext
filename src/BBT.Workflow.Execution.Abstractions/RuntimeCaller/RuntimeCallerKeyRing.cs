using System.Text.Json;
using System.Text.RegularExpressions;

namespace BBT.Workflow.Execution.RuntimeCaller;

/// <summary>
/// The HMAC keys a runtime proves its own calls with. The active key signs; every key in the ring
/// verifies, so a rotation adds a key everywhere, then moves <see cref="ActiveKeyId"/>, then removes
/// the old key.
/// <para>
/// The ring is read from a mounted file and never from configuration: flow scripts can read
/// <c>IConfiguration</c>, the Dapr secret store and environment variables, but not a file outside
/// the sandbox. Parse failures never echo key material.
/// </para>
/// </summary>
public sealed partial class RuntimeCallerKeyRing
{
    /// <summary>The shortest key accepted, in bytes (the HMAC-SHA256 output size).</summary>
    public const int MinimumKeyBytes = 32;

    /// <summary>The most keys a ring may hold.</summary>
    public const int MaximumKeys = 16;

    private readonly Dictionary<string, byte[]> _keys;

    private RuntimeCallerKeyRing(string activeKeyId, Dictionary<string, byte[]> keys)
    {
        ActiveKeyId = activeKeyId;
        _keys = keys;
    }

    /// <summary>The id of the key that signs outgoing calls.</summary>
    public string ActiveKeyId { get; }

    /// <summary>How many keys verify.</summary>
    public int Count => _keys.Count;

    /// <summary>The key that signs outgoing calls.</summary>
    public byte[] ActiveKey => _keys[ActiveKeyId];

    /// <summary>Whether <paramref name="keyId"/> has the key-id shape (1-64 of letters, digits, '.', '_', '-').</summary>
    public static bool IsWellFormedKeyId(string? keyId) => keyId is not null && KeyIdPattern().IsMatch(keyId);

    /// <summary>Looks up a verifying key by id (ordinal).</summary>
    public bool TryGetKey(string keyId, out byte[] key)
    {
        if (_keys.TryGetValue(keyId, out var found))
        {
            key = found;
            return true;
        }

        key = [];
        return false;
    }

    /// <summary>
    /// Parses <c>{ "activeKeyId": "k2", "keys": { "k2": "&lt;base64&gt;", "k1": "&lt;base64&gt;" } }</c>.
    /// </summary>
    /// <exception cref="FormatException">The document is not a valid key ring. The message never contains key material.</exception>
    public static RuntimeCallerKeyRing Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new FormatException("The runtime-caller key ring is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("The runtime-caller key ring must be a JSON object.");

            if (!root.TryGetProperty("activeKeyId", out var activeElement)
                || activeElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(activeElement.GetString()))
                throw new FormatException("The runtime-caller key ring has no 'activeKeyId'.");

            if (!root.TryGetProperty("keys", out var keysElement) || keysElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("The runtime-caller key ring has no 'keys' object.");

            var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in keysElement.EnumerateObject())
            {
                if (!IsWellFormedKeyId(entry.Name))
                    throw new FormatException(
                        "A runtime-caller key id must be 1-64 characters of letters, digits, '.', '_' or '-'.");
                if (entry.Value.ValueKind != JsonValueKind.String)
                    throw new FormatException($"Runtime-caller key '{entry.Name}' must be a base64 string.");

                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(entry.Value.GetString()!);
                }
                catch (FormatException)
                {
                    throw new FormatException($"Runtime-caller key '{entry.Name}' is not valid base64.");
                }

                if (bytes.Length < MinimumKeyBytes)
                    throw new FormatException(
                        $"Runtime-caller key '{entry.Name}' is shorter than {MinimumKeyBytes} bytes.");
                if (!keys.TryAdd(entry.Name, bytes))
                    throw new FormatException($"Runtime-caller key '{entry.Name}' appears twice.");
            }

            if (keys.Count == 0)
                throw new FormatException("The runtime-caller key ring holds no keys.");
            if (keys.Count > MaximumKeys)
                throw new FormatException($"The runtime-caller key ring holds more than {MaximumKeys} keys.");

            var activeKeyId = activeElement.GetString()!;
            if (!keys.ContainsKey(activeKeyId))
                throw new FormatException($"The active runtime-caller key '{activeKeyId}' is not in the ring.");

            return new RuntimeCallerKeyRing(activeKeyId, keys);
        }
    }

    // \A..\z, not ^..$: `$` also matches before a final newline, and an unknown key id is client text
    // that reaches a span tag.
    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();
}
