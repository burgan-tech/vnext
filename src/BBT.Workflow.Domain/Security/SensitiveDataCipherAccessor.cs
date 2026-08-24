using BBT.Workflow.Definitions.Schemas;

namespace BBT.Workflow.Security;

/// <summary>
/// The process-wide cipher used to decrypt instance data on read.
/// <para>
/// This is the single piece of ambient state in the encryption design, and it is confined to the
/// <b>decrypt</b> direction on purpose. Decryption is a pure function of the ciphertext marker plus
/// process-wide key material: no schema, no request scope, no tenant. That is what makes a static
/// hook acceptable here where it would not be for encryption, which genuinely needs per-request
/// schema context and therefore lives in the write funnel instead.
/// </para>
/// <para>
/// It exists because <c>InstanceData</c> is materialised by EF and constructed in raw-SQL paths, so
/// it can never be handed a dependency by the container — and putting decryption anywhere else
/// means auditing twenty-odd read sites and hoping none is ever added without it.
/// </para>
/// </summary>
public static class SensitiveDataCipherAccessor
{
    private static ISensitiveDataCipher _current = NullSensitiveDataCipher.Instance;

    /// <summary>
    /// The active cipher. Defaults to <see cref="NullSensitiveDataCipher"/>, which passes plaintext
    /// through untouched and refuses to silently pass ciphertext through.
    /// </summary>
    public static ISensitiveDataCipher Current => _current;

    /// <summary>
    /// Installs the cipher. Called once during host startup.
    /// </summary>
    /// <param name="cipher">The cipher to install.</param>
    public static void Configure(ISensitiveDataCipher cipher)
        => _current = cipher ?? throw new ArgumentNullException(nameof(cipher));

    /// <summary>
    /// Restores the default no-op cipher. For tests.
    /// </summary>
    public static void Reset() => _current = NullSensitiveDataCipher.Instance;
}

/// <summary>
/// The cipher used when encryption was never configured for this host.
/// <para>
/// Identity on plaintext, and a hard failure on ciphertext. Passing a marker through would be the
/// worst possible outcome — a client receiving <c>enc:v1:...</c> as if it were the value — so a
/// misconfigured host that holds encrypted rows fails loudly instead.
/// </para>
/// </summary>
public sealed class NullSensitiveDataCipher : ISensitiveDataCipher
{
    /// <summary>Singleton instance.</summary>
    public static readonly NullSensitiveDataCipher Instance = new();

    private NullSensitiveDataCipher()
    {
    }

    /// <inheritdoc />
    public bool IsEnabled => false;

    /// <inheritdoc />
    public JsonData Encrypt(JsonData plaintext, IReadOnlyDictionary<string, SensitiveFieldMetadata> sensitiveFields)
        => plaintext;

    /// <inheritdoc />
    public JsonData Decrypt(JsonData stored)
    {
        if (ISensitiveDataCipher.ContainsCiphertext(stored.Json))
        {
            throw new SensitiveDataEncryptionException(
                "Encrypted instance data was read but no cipher is configured in this host. " +
                $"Configure '{DataEncryptionOptions.SectionName}' so the keys are available, or the " +
                "ciphertext would be served to callers as if it were the value.");
        }

        return stored;
    }
}
