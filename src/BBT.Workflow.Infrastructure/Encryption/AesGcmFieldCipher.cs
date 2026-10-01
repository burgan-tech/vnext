using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Encryption;

/// <summary>Why a token could not be opened. Never carries key material or the value.</summary>
public enum TokenFailure
{
    /// <summary>Opened.</summary>
    None = 0,

    /// <summary>Not four segments, not base64url, wrong version byte, unknown key version, or too short.</summary>
    Malformed,

    /// <summary>The instance has no secret (it was deleted — crypto-shredded — or the lookup failed).</summary>
    UnknownKey,

    /// <summary>GCM authentication failed: tampered, moved to another instance/path, or sealed under another key.</summary>
    Tampered,
}

/// <summary>
/// The cryptography of <c>x-encryption</c>, keyed by the instance's own secrets (<see cref="InstanceSecret"/>).
/// <para>
/// <b>encrypt.</b> AES-256-GCM with the instance key, 96-bit random nonce, 128-bit tag. Token
/// <c>ENCRYPTED:AES256:i1:&lt;base64url(0x01 ‖ nonce ‖ ciphertext ‖ tag)&gt;</c> — <c>i1</c> names the scheme (instance key,
/// version 1). The additional authenticated data is <c>"vnext.idata.v1|i1|" + instanceId + "|" + path</c>, so a token copied
/// to another field or instance does not open. It is deliberately not bound to the row: an unchanged value carries its
/// token forward to the next version (every version is a full copy) and a client can echo it back.
/// </para>
/// <para>
/// <b>hash.</b> HMAC-SHA256 (or -512) keyed by the instance salt, lowercase hex, <c>HASHED:SHA256:&lt;hex&gt;</c>.
/// Deterministic within an instance (so an unchanged value keeps its digest and dedup stays exact), different across
/// instances by construction.
/// </para>
/// </summary>
internal static class AesGcmFieldCipher
{
    /// <summary>The key-version segment of every token this runtime writes.</summary>
    public const string KeyVersion = "i1";

    private const byte Version = 0x01;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] HashKeyInfo = "vnext/datahash/v1"u8.ToArray();
    private static readonly string TokenPrefix = EncryptedValueFormat.Prefix + KeyVersion + ":";

    /// <summary>Encrypts <paramref name="plaintext"/> with the instance key.</summary>
    public static string Encrypt(byte[] key, Guid instanceId, string path, string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var payload = new byte[1 + NonceSize + plain.Length + TagSize];
        payload[0] = Version;
        var nonce = payload.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        var cipher = payload.AsSpan(1 + NonceSize, plain.Length);
        var tag = payload.AsSpan(1 + NonceSize + plain.Length, TagSize);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag, Aad(instanceId, path));

        return string.Concat(TokenPrefix, Base64Url.EncodeToString(payload));
    }

    /// <summary>True when <paramref name="token"/> was written under this runtime's key version.</summary>
    public static bool IsCurrentVersion(string token) => token.StartsWith(TokenPrefix, StringComparison.Ordinal);

    /// <summary>Opens a token with the instance key.</summary>
    public static TokenFailure TryDecrypt(string token, byte[] key, Guid instanceId, string path, out string plaintext)
    {
        plaintext = string.Empty;
        if (!IsCurrentVersion(token))
            return TokenFailure.Malformed;

        var encoded = token.AsSpan(TokenPrefix.Length);
        if (encoded.IndexOf(':') >= 0)
            return TokenFailure.Malformed;

        byte[] payload;
        try
        {
            payload = Base64Url.DecodeFromChars(encoded);
        }
        catch (FormatException)
        {
            return TokenFailure.Malformed;
        }

        if (payload.Length < 1 + NonceSize + TagSize || payload[0] != Version)
            return TokenFailure.Malformed;

        var nonce = payload.AsSpan(1, NonceSize);
        var cipherLength = payload.Length - 1 - NonceSize - TagSize;
        var cipher = payload.AsSpan(1 + NonceSize, cipherLength);
        var tag = payload.AsSpan(1 + NonceSize + cipherLength, TagSize);
        var plain = new byte[cipherLength];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain, Aad(instanceId, path));
        }
        catch (CryptographicException)
        {
            return TokenFailure.Tampered;
        }

        plaintext = Encoding.UTF8.GetString(plain);
        return TokenFailure.None;
    }

    /// <summary><c>HASHED:SHA256:&lt;hex&gt;</c> (or <c>SHA512</c>) of <paramref name="value"/> under the instance salt.</summary>
    public static string Hash(byte[] salt, string value, string algorithm)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return algorithm == FieldMaskRule.Sha512
            ? EncryptedValueFormat.Sha512HashPrefix + Convert.ToHexStringLower(HMACSHA512.HashData(salt, bytes))
            : EncryptedValueFormat.Sha256HashPrefix + Convert.ToHexStringLower(HMACSHA256.HashData(salt, bytes));
    }

    /// <summary>
    /// Keyed digest of a document's normalized form, 40 lowercase hex characters (the <c>DataHash</c> column width). Replaces
    /// the unkeyed SHA-1 for rows that carry tokens: an unkeyed digest of the plaintext would let anyone who can read the row
    /// confirm a guessed low-entropy value.
    /// </summary>
    public static string KeyedHash(byte[] key, string normalizedJson)
    {
        var hashKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, key, InstanceSecret.SecretSize, salt: null, info: HashKeyInfo);
        return Convert.ToHexStringLower(HMACSHA256.HashData(hashKey, Encoding.UTF8.GetBytes(normalizedJson)))[..40];
    }

    private static byte[] Aad(Guid instanceId, string path) =>
        Encoding.UTF8.GetBytes($"vnext.idata.v1|{KeyVersion}|{instanceId:N}|{path}");
}
