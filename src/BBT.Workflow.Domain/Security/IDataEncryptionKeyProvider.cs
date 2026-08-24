namespace BBT.Workflow.Security;

/// <summary>
/// A named 256-bit data encryption key.
/// </summary>
/// <param name="KeyId">
/// Identifier written into the ciphertext marker. Rotation works by writing a new
/// <c>KeyId</c> while old ones stay loadable, so no bulk re-encryption is needed to roll a key.
/// </param>
/// <param name="Key">The raw 32-byte key.</param>
public sealed record DataEncryptionKey(string KeyId, byte[] Key)
{
    /// <summary>AES-256 key length in bytes.</summary>
    public const int RequiredKeyLength = 32;
}

/// <summary>
/// Supplies key material for instance-data encryption.
/// <para>
/// Deliberately split into an <b>async load</b> and <b>synchronous lookups</b>. Decryption happens
/// behind a property getter (<c>InstanceData.Data</c>) which cannot await, and sync-over-async
/// there would deadlock under load. So every key is loaded up front — the Dapr secret store is
/// configured with <c>vaultValueType: map</c>, so one fetch returns the whole key set, including
/// retired ids — and lookups afterwards are pure in-memory reads.
/// </para>
/// </summary>
public interface IDataEncryptionKeyProvider
{
    /// <summary>
    /// Loads (or reloads) all key material. Called once at startup and again only if a key is
    /// missing, so a key added after boot can be picked up without a restart.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The key new ciphertext is written with.
    /// </summary>
    /// <returns>The active key, or null when encryption is disabled or unconfigured.</returns>
    DataEncryptionKey? GetActive();

    /// <summary>
    /// Resolves a key by the id embedded in a ciphertext marker.
    /// </summary>
    /// <param name="keyId">Key id from the marker.</param>
    /// <param name="key">The resolved key.</param>
    /// <returns><c>true</c> when the key is loaded and available.</returns>
    bool TryGet(string keyId, out DataEncryptionKey key);
}
