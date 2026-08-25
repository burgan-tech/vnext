namespace BBT.Workflow.Security;

/// <summary>
/// Configuration for instance-data encryption at rest.
/// Bound from the <c>Workflow:Security:Encryption</c> configuration section.
/// </summary>
/// <remarks>
/// Defaults to <b>off</b>. With encryption disabled, <c>x-sensitive</c> still drives masking and
/// log redaction — the two protections are independent, so a deployment can adopt redaction long
/// before it provisions keys.
/// </remarks>
public sealed class DataEncryptionOptions
{
    /// <summary>Configuration section name this options class binds from.</summary>
    public const string SectionName = "Workflow:Security:Encryption";

    /// <summary>
    /// Master switch. When false, values annotated <c>encryptAtRest</c> are stored as plaintext
    /// and no key material is required. Existing ciphertext is still decrypted on read, so
    /// turning this off is safe and does not strand already-encrypted rows.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Key id used for new encryptions. Must exist in the configured key source. Ignored when
    /// <see cref="Enabled"/> is false.
    /// </summary>
    public string? ActiveKeyId { get; set; }

    /// <summary>
    /// Where key material comes from. <c>Configuration</c> reads <see cref="Keys"/> directly and is
    /// intended for local development and tests; <c>DaprSecretStore</c> reads the Dapr secret store
    /// named by <see cref="SecretStoreName"/>.
    /// </summary>
    public DataEncryptionKeySource KeySource { get; set; } = DataEncryptionKeySource.Configuration;

    /// <summary>
    /// Key id → base64-encoded 256-bit key. Used when <see cref="KeySource"/> is
    /// <see cref="DataEncryptionKeySource.Configuration"/>.
    /// </summary>
    /// <remarks>
    /// Never put production key material here. Configuration is captured by diagnostics dumps and
    /// checked-in appsettings files; that is precisely the exposure encryption exists to remove.
    /// </remarks>
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Dapr secret store component name. Defaults to the component every host already ships.
    /// </summary>
    public string SecretStoreName { get; set; } = "vnext-secret";

    /// <summary>
    /// Secret name holding the key map inside the secret store. The store is configured with
    /// <c>vaultValueType: map</c>, so one fetch returns every key id at once — which is what lets
    /// decryption stay synchronous.
    /// </summary>
    public string SecretName { get; set; } = "workflow-secret";

    /// <summary>
    /// Prefix distinguishing encryption keys from other entries in the same secret map. A secret
    /// named <c>dataKey.v1</c> is loaded as key id <c>v1</c>.
    /// </summary>
    public string SecretKeyPrefix { get; set; } = "dataKey.";
}

/// <summary>
/// Where <see cref="IDataEncryptionKeyProvider"/> loads key material from.
/// </summary>
public enum DataEncryptionKeySource
{
    /// <summary>From <see cref="DataEncryptionOptions.Keys"/>. Development and tests only.</summary>
    Configuration = 0,

    /// <summary>From the Dapr secret store (HashiCorp Vault in the shipped compose files).</summary>
    DaprSecretStore = 1
}
