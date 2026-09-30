using System.Text.Encodings.Web;
using System.Text.Json;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BBT.Workflow.Encryption;

/// <summary>
/// Opens and seals the <c>x-encryption</c> values of instance data (singleton), with the instance's own secrets.
/// <list type="bullet">
/// <item><see cref="Unprotect"/> — load path: decrypts every <c>encrypt</c> token reached through object properties.
/// Driven by the token prefix, never by the current schema. The secret comes from the in-process cache (a preload at the
/// entry point normally put it there) or, failing that, a synchronous lookup; without one the tokens stay closed.</item>
/// <item><see cref="SanitizeDelta"/> — write path, before the merge: a request may only send back the value already stored
/// at the same path (an echoed token becomes the head's plaintext, an echoed digest stays); any other value carrying a
/// reserved prefix is rejected, so nobody can plant a token or a digest or replay one from elsewhere.</item>
/// <item><see cref="ApplyHashes"/> — write path, after the merge and before dedup: <c>hash</c> paths are replaced by their
/// digest. Irreversible; a value that already is a digest is kept.</item>
/// <item><see cref="Protect"/> — write path, after validation: encrypts the <c>encrypt</c> paths. An unchanged value keeps
/// the head's token; a value whose head token cannot be opened is refused rather than written back as if it were
/// plaintext.</item>
/// </list>
/// Paths are dot-joined property names — the convention <c>x-roles</c> and the read filter use. Arrays add no segment and
/// are never walked: both keywords are admitted only on properties reachable through <c>properties</c>.
/// </summary>
public sealed class InstanceDataProtector(
    InstanceSecretStore secrets,
    ILogger<InstanceDataProtector>? logger = null) : IInstanceDataProtector
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    /// <summary>The secret store.</summary>
    public InstanceSecretStore Secrets => secrets;

    /// <inheritdoc />
    public InstanceDataView Unprotect(string? schema, Guid instanceId, JsonData stored)
    {
        if (!EncryptedValueFormat.MayContainToken(stored.Json))
            return InstanceDataView.Of(stored);

        var fromCache = secrets.TryGetCached(schema, instanceId) is not null;
        var view = Open(instanceId, stored, secrets.TryLoad(schema, instanceId));

        // A cached secret that fails to authenticate a token can only be one left behind by a first write whose
        // transaction rolled back: drop it and read the row that actually committed.
        if (fromCache && view.Undecryptable.Count > 0)
        {
            secrets.Evict(schema, instanceId);
            view = Open(instanceId, stored, secrets.TryLoad(schema, instanceId));
        }

        return view;
    }

    private InstanceDataView Open(Guid instanceId, JsonData stored, InstanceSecretMaterial? secret)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        var undecryptable = new HashSet<string>(StringComparer.Ordinal);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(stored.Json.Length);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteUnprotected(stored.JsonElement, string.Empty, writer, instanceId, secret, tokens, undecryptable);
        }

        if (tokens.Count == 0 && undecryptable.Count == 0)
            return InstanceDataView.Of(stored);

        var plain = new JsonData(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
        return new InstanceDataView(plain, tokens, undecryptable);
    }

    /// <summary>
    /// Replaces an echoed token with the head's plaintext, keeps an echoed digest, and rejects every other reserved-prefix
    /// value in <paramref name="delta"/>. Returns <paramref name="delta"/> itself when it holds no reserved prefix.
    /// <para>
    /// <c>ENCRYPTED:</c> is reserved everywhere (decryption is prefix-driven, so a planted token anywhere would be opened).
    /// <c>HASHED:</c> is reserved only on the schema's hash paths (<paramref name="hashPaths"/>): elsewhere it is an
    /// ordinary string — and a legitimate one, since the engine sees digests and a mapping may copy one.
    /// </para>
    /// </summary>
    /// <exception cref="EncryptedValueReservedException">A value carries a reserved prefix and is not the stored one.</exception>
    public JsonData SanitizeDelta(
        Guid instanceId, JsonData delta, JsonData? headStored, InstanceDataView? head,
        IReadOnlyDictionary<string, string>? hashPaths = null)
    {
        if (!EncryptedValueFormat.MayContainReserved(delta.Json))
            return delta;

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(delta.Json.Length);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteSanitized(delta.JsonElement, string.Empty, writer, instanceId, headStored?.JsonElement, head, hashPaths, insideArray: false);
        }

        return new JsonData(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    /// <summary>
    /// Replaces every string at a <paramref name="hashPaths"/> path by its digest under the instance salt. A value that
    /// already is a digest (carried unchanged from the head) is kept. Returns <paramref name="content"/> itself when
    /// nothing changed.
    /// </summary>
    public JsonData ApplyHashes(JsonData content, IReadOnlyDictionary<string, string> hashPaths, InstanceSecretMaterial secret)
    {
        if (hashPaths.Count == 0)
            return content;

        var changed = false;
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(content.Json.Length * 2);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            Write(content.JsonElement, string.Empty, writer);
        }

        return changed ? new JsonData(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan)) : content;

        void Write(JsonElement node, string path, Utf8JsonWriter writer)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                node.WriteTo(writer);
                return;
            }

            writer.WriteStartObject();
            foreach (var property in node.EnumerateObject())
            {
                var childPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                writer.WritePropertyName(property.Name);

                if (property.Value.ValueKind == JsonValueKind.String &&
                    hashPaths.TryGetValue(childPath, out var algorithm) &&
                    !EncryptedValueFormat.IsHashed(property.Value.GetString()))
                {
                    writer.WriteStringValue(AesGcmFieldCipher.Hash(secret.HashSalt, property.Value.GetString()!, algorithm));
                    changed = true;
                }
                else
                {
                    Write(property.Value, childPath, writer);
                }
            }

            writer.WriteEndObject();
        }
    }

    /// <summary>
    /// Encrypts the <paramref name="encryptPaths"/> of <paramref name="plain"/> (a merged, hashed, validated document).
    /// Returns the stored form and the view the engine keeps. When there is nothing to encrypt the stored form is
    /// <paramref name="plain"/> itself.
    /// </summary>
    /// <exception cref="EncryptionKeyUnavailableException">
    /// An unchanged value whose head token cannot be opened (writing it back would store the token string as the value).
    /// </exception>
    public (JsonData Stored, InstanceDataView View) Protect(
        Guid instanceId, JsonData plain, IReadOnlySet<string> encryptPaths, InstanceDataView? head, InstanceSecretMaterial secret)
    {
        if (encryptPaths.Count == 0)
            return (plain, InstanceDataView.Of(plain));

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(plain.Json.Length * 2);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteProtected(plain.JsonElement, string.Empty, writer);
        }

        if (tokens.Count == 0)
            return (plain, InstanceDataView.Of(plain));

        var stored = new JsonData(System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan));
        return (stored, new InstanceDataView(plain, tokens));

        void WriteProtected(JsonElement node, string path, Utf8JsonWriter writer)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                node.WriteTo(writer);
                return;
            }

            writer.WriteStartObject();
            foreach (var property in node.EnumerateObject())
            {
                var childPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                writer.WritePropertyName(property.Name);

                if (property.Value.ValueKind == JsonValueKind.String && encryptPaths.Contains(childPath))
                {
                    var token = TokenFor(childPath, property.Value.GetString()!);
                    tokens[childPath] = token;
                    writer.WriteStringValue(token);
                }
                else
                {
                    WriteProtected(property.Value, childPath, writer);
                }
            }

            writer.WriteEndObject();
        }

        string TokenFor(string path, string value)
        {
            if (head is not null)
            {
                var headValue = EncryptedValueFormat.StringAt(head.Plain.JsonElement, path);
                if (headValue is not null && string.Equals(headValue, value, StringComparison.Ordinal))
                {
                    if (head.Tokens.TryGetValue(path, out var carried))
                        return carried;

                    // The head holds this very string as an unopenable token: it is not a value.
                    if (head.Undecryptable.Contains(path))
                        throw new EncryptionKeyUnavailableException(instanceId, path);
                }
            }

            return AesGcmFieldCipher.Encrypt(secret.EncryptionKey, instanceId, path, value);
        }
    }

    /// <summary><c>DataHash</c> for a row that carries tokens: a keyed digest under the instance key.</summary>
    public static string KeyedDataHash(JsonData plain, InstanceSecretMaterial secret) =>
        AesGcmFieldCipher.KeyedHash(secret.EncryptionKey, plain.NormalizedJson);

    // ── walkers ──────────────────────────────────────────────────────────────

    private void WriteUnprotected(
        JsonElement node,
        string path,
        Utf8JsonWriter writer,
        Guid instanceId,
        InstanceSecretMaterial? secret,
        Dictionary<string, string> tokens,
        HashSet<string> undecryptable)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            node.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (var property in node.EnumerateObject())
        {
            var childPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
            writer.WritePropertyName(property.Name);

            if (property.Value.ValueKind == JsonValueKind.String &&
                property.Value.GetString() is { } value &&
                EncryptedValueFormat.IsToken(value))
            {
                var plaintext = string.Empty;
                var failure = secret is null
                    ? TokenFailure.UnknownKey
                    : AesGcmFieldCipher.TryDecrypt(value, secret.EncryptionKey, instanceId, childPath, out plaintext);

                if (failure == TokenFailure.None)
                {
                    tokens[childPath] = value;
                    writer.WriteStringValue(plaintext);
                }
                else
                {
                    undecryptable.Add(childPath);
                    _logger.EncryptedValueUndecryptable(instanceId, childPath, AesGcmFieldCipher.KeyVersion, failure.ToString());
                    writer.WriteStringValue(value);
                }
            }
            else
            {
                WriteUnprotected(property.Value, childPath, writer, instanceId, secret, tokens, undecryptable);
            }
        }

        writer.WriteEndObject();
    }


    private void WriteSanitized(
        JsonElement node,
        string path,
        Utf8JsonWriter writer,
        Guid instanceId,
        JsonElement? headStored,
        InstanceDataView? head,
        IReadOnlyDictionary<string, string>? hashPaths,
        bool insideArray)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in node.EnumerateObject())
                {
                    var childPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                    writer.WritePropertyName(property.Name);
                    WriteSanitized(property.Value, childPath, writer, instanceId, headStored, head, hashPaths, insideArray);
                }
                writer.WriteEndObject();
                return;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in node.EnumerateArray())
                    WriteSanitized(item, path, writer, instanceId, headStored, head, hashPaths, insideArray: true);
                writer.WriteEndArray();
                return;

            case JsonValueKind.String when EncryptedValueFormat.IsHashed(node.GetString()) &&
                                           (insideArray || hashPaths is null || !hashPaths.ContainsKey(path)):
                // A digest outside a hash path is an ordinary value (e.g. a mapping copying a hashed field).
                node.WriteTo(writer);
                return;

            case JsonValueKind.String when EncryptedValueFormat.IsReserved(node.GetString()):
                var value = node.GetString()!;
                if (!insideArray && headStored is { } stored &&
                    string.Equals(EncryptedValueFormat.StringAt(stored, path), value, StringComparison.Ordinal))
                {
                    // An echoed digest is the stored value itself.
                    if (EncryptedValueFormat.IsHashed(value))
                    {
                        writer.WriteStringValue(value);
                        return;
                    }

                    // An echoed token is a no-op: put the plaintext back so the merge sees "unchanged" and the funnel
                    // carries the token forward. An unopenable one is kept (the funnel refuses to write it).
                    var plain = head is not null ? EncryptedValueFormat.StringAt(head.Plain.JsonElement, path) : null;
                    writer.WriteStringValue(plain ?? value);
                    return;
                }

                _logger.EncryptedValueRejectedOnWrite(instanceId, insideArray ? path + "[]" : path);
                throw new EncryptedValueReservedException(insideArray ? path + "[]" : path);

            default:
                node.WriteTo(writer);
                return;
        }
    }

    /// <summary>String value at a dot path of <paramref name="root"/>, or null.</summary>
    internal static string? TryGetString(JsonElement root, string path) => EncryptedValueFormat.StringAt(root, path);
}
