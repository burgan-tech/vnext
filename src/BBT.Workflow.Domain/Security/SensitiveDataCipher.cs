using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BBT.Workflow.Definitions.Schemas;

namespace BBT.Workflow.Security;

/// <summary>
/// Encrypts and decrypts the <c>encryptAtRest</c> leaves of an instance-data document.
/// <para>
/// The two directions are deliberately asymmetric, and that asymmetry is what makes the whole
/// design work. <b>Encryption needs the schema</b> (which paths are sensitive), so it can only
/// happen where the master schema is in hand — the instance-data write funnel.
/// <b>Decryption needs nothing but the ciphertext</b>, because the marker names its own algorithm
/// and key id. That is why decryption can sit on a property getter reached from twenty different
/// call sites without any of them knowing a schema exists.
/// </para>
/// </summary>
public interface ISensitiveDataCipher
{
    /// <summary>True when new values will actually be encrypted.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Replaces every <c>encryptAtRest</c> leaf with its ciphertext marker.
    /// </summary>
    /// <param name="plaintext">The full merged document about to be stored.</param>
    /// <param name="sensitiveFields">Path → metadata from the master schema.</param>
    /// <returns>The document to persist. Returns the input unchanged when nothing applies.</returns>
    JsonData Encrypt(JsonData plaintext, IReadOnlyDictionary<string, SensitiveFieldMetadata> sensitiveFields);

    /// <summary>
    /// Replaces every ciphertext marker anywhere in the document with its plaintext.
    /// Schema-free: driven entirely by the marker. Idempotent on plaintext input.
    /// </summary>
    /// <param name="stored">The document as read from storage.</param>
    /// <returns>The plaintext document, or the input unchanged when it holds no markers.</returns>
    JsonData Decrypt(JsonData stored);

    /// <summary>
    /// Cheap check for whether a raw JSON string holds any ciphertext, used to skip work and to
    /// assert the no-ciphertext-escapes invariant.
    /// </summary>
    /// <param name="json">Raw JSON text.</param>
    /// <returns><c>true</c> when a ciphertext marker is present.</returns>
    static bool ContainsCiphertext(string? json)
        => json is not null && json.Contains(SensitiveDataCipher.MarkerPrefix, StringComparison.Ordinal);
}

/// <summary>
/// AES-256-GCM implementation of <see cref="ISensitiveDataCipher"/>.
/// </summary>
/// <param name="keyProvider">Supplies the active key for writes and any key id for reads.</param>
/// <param name="isEnabled">Whether new values should be encrypted.</param>
public sealed class SensitiveDataCipher(IDataEncryptionKeyProvider keyProvider, bool isEnabled) : ISensitiveDataCipher
{
    /// <summary>
    /// Marker prefix. Chosen to be recognisable in a database dump, a log line, and an assertion —
    /// the escape test greps for exactly this.
    /// </summary>
    public const string MarkerPrefix = "enc:v1:";

    private const int NonceLength = 12;  // 96-bit, the GCM-recommended size
    private const int TagLength = 16;    // 128-bit authentication tag

    /// <inheritdoc />
    public bool IsEnabled => isEnabled && keyProvider.GetActive() is not null;

    /// <inheritdoc />
    public JsonData Encrypt(JsonData plaintext, IReadOnlyDictionary<string, SensitiveFieldMetadata> sensitiveFields)
    {
        if (!isEnabled || sensitiveFields.Count == 0)
            return plaintext;

        var encryptedPaths = sensitiveFields
            .Where(pair => pair.Value.EncryptAtRest)
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

        if (encryptedPaths.Count == 0)
            return plaintext;

        var key = keyProvider.GetActive()
                  ?? throw new SensitiveDataEncryptionException(
                      "Instance-data encryption is enabled but no active key is available. Check " +
                      $"'{DataEncryptionOptions.SectionName}:ActiveKeyId' and the configured key source.");

        var root = plaintext.JsonElement;
        if (root.ValueKind != JsonValueKind.Object)
            return plaintext;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteTransformed(root, string.Empty, writer, encryptedPaths, key, encrypting: true);
        }

        return new JsonData(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <inheritdoc />
    public JsonData Decrypt(JsonData stored)
    {
        // Decryption is NOT gated on isEnabled: turning encryption off must never strand rows that
        // were written while it was on.
        if (!ISensitiveDataCipher.ContainsCiphertext(stored.Json))
            return stored;

        var root = stored.JsonElement;
        if (root.ValueKind != JsonValueKind.Object)
            return stored;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteTransformed(root, string.Empty, writer, paths: null, key: null, encrypting: false);
        }

        return new JsonData(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>
    /// Single recursive rewrite used by both directions. Paths follow
    /// <see cref="SchemaAnnotationWalker"/> — dotted, with <c>[]</c> for array items — so the
    /// document walk and the schema walk cannot drift apart.
    /// </summary>
    private void WriteTransformed(
        JsonElement node,
        string path,
        Utf8JsonWriter writer,
        HashSet<string>? paths,
        DataEncryptionKey? key,
        bool encrypting)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in node.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    var childPath = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";
                    WriteTransformed(property.Value, childPath, writer, paths, key, encrypting);
                }

                writer.WriteEndObject();
                return;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in node.EnumerateArray())
                {
                    // "[]" marks the item level, matching how the schema addresses array members.
                    WriteTransformed(item, $"{path}[]", writer, paths, key, encrypting);
                }

                writer.WriteEndArray();
                return;

            case JsonValueKind.String when encrypting && paths!.Contains(path):
                var raw = node.GetString();
                if (string.IsNullOrEmpty(raw) || raw.StartsWith(MarkerPrefix, StringComparison.Ordinal))
                {
                    // Already-encrypted input is left alone, which is what makes a backfill
                    // re-runnable and an append over an encrypted head safe.
                    node.WriteTo(writer);
                    return;
                }

                writer.WriteStringValue(EncryptValue(raw, path, key!));
                return;

            case JsonValueKind.String when !encrypting:
                var text = node.GetString();
                if (text is not null && text.StartsWith(MarkerPrefix, StringComparison.Ordinal))
                {
                    writer.WriteStringValue(DecryptValue(text, path));
                    return;
                }

                node.WriteTo(writer);
                return;

            default:
                node.WriteTo(writer);
                return;
        }
    }

    /// <summary>
    /// Produces <c>enc:v1:{keyId}:{base64url(nonce ‖ ciphertext ‖ tag)}</c>. The field path is bound
    /// as additional authenticated data, so a ciphertext moved to a different field fails to
    /// authenticate rather than decrypting into the wrong place.
    /// </summary>
    private static string EncryptValue(string plaintext, string path, DataEncryptionKey key)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagLength];

        using var aes = new AesGcm(key.Key, TagLength);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag, Aad(path));

        var payload = new byte[NonceLength + cipherBytes.Length + TagLength];
        nonce.CopyTo(payload, 0);
        cipherBytes.CopyTo(payload, NonceLength);
        tag.CopyTo(payload, NonceLength + cipherBytes.Length);

        return $"{MarkerPrefix}{key.KeyId}:{Base64Url.EncodeToString(payload)}";
    }

    private string DecryptValue(string marker, string path)
    {
        if (!TryParseMarker(marker, out var keyId, out var payload))
            throw new SensitiveDataEncryptionException($"Malformed ciphertext marker at '{path}'.");

        if (!keyProvider.TryGet(keyId, out var key))
        {
            throw new SensitiveDataEncryptionException(
                $"No key '{keyId}' is loaded, so the value at '{path}' cannot be decrypted. A key " +
                "that has written data must remain available for as long as that data exists.");
        }

        if (payload.Length < NonceLength + TagLength)
            throw new SensitiveDataEncryptionException($"Truncated ciphertext at '{path}'.");

        var cipherLength = payload.Length - NonceLength - TagLength;
        var nonce = payload.AsSpan(0, NonceLength);
        var cipherBytes = payload.AsSpan(NonceLength, cipherLength);
        var tag = payload.AsSpan(NonceLength + cipherLength, TagLength);
        var plainBytes = new byte[cipherLength];

        try
        {
            using var aes = new AesGcm(key.Key, TagLength);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes, Aad(path));
        }
        catch (CryptographicException ex)
        {
            // Authentication failure means tampering, a wrong key, or a value relocated to another
            // field. Never fall back to returning the marker: that would leak ciphertext to a client.
            throw new SensitiveDataEncryptionException(
                $"Ciphertext at '{path}' failed authentication. The value may have been tampered " +
                "with, encrypted under a different field, or written with a different key.", ex);
        }

        return Encoding.UTF8.GetString(plainBytes);
    }

    private static bool TryParseMarker(
        string marker,
        [NotNullWhen(true)] out string? keyId,
        [NotNullWhen(true)] out byte[]? payload)
    {
        keyId = null;
        payload = null;

        var rest = marker.AsSpan(MarkerPrefix.Length);
        var separator = rest.IndexOf(':');
        if (separator <= 0 || separator == rest.Length - 1)
            return false;

        keyId = rest[..separator].ToString();

        try
        {
            payload = Base64Url.DecodeFromChars(rest[(separator + 1)..]);
            return true;
        }
        catch (FormatException)
        {
            keyId = null;
            return false;
        }
    }

    private static byte[] Aad(string path) => Encoding.UTF8.GetBytes(path);
}
