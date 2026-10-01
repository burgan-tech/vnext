namespace BBT.Workflow.Encryption;

/// <summary>
/// The per-instance secrets of <c>x-encryption</c>: the AES-256 key that seals <c>encrypt</c> values and the salt that keys
/// <c>hash</c> digests. One row per instance, in the instance's own flow schema (<c>InstanceSecrets</c>), created by the
/// write funnel the first time the instance writes a protected field and never changed afterwards. Deleted with the
/// instance (cascade) — deleting the row alone makes the instance's encrypted values unrecoverable (crypto-shredding).
/// <para>
/// Stored in plaintext by decision: database security is owned by the DB team, and the runtime never serves these values
/// on any API, log, span or cache other than its own in-process L1.
/// </para>
/// </summary>
public sealed class InstanceSecret
{
    /// <summary>Key size in bytes (AES-256) and salt size.</summary>
    public const int SecretSize = 32;

    /// <summary>The instance the secrets belong to (primary key, cascade-deleted with it).</summary>
    public Guid InstanceId { get; set; }

    /// <summary>AES-256-GCM key of the instance's <c>encrypt</c> values.</summary>
    public byte[] EncryptionKey { get; set; } = [];

    /// <summary>HMAC key (salt) of the instance's <c>hash</c> digests.</summary>
    public byte[] HashSalt { get; set; } = [];

    /// <summary>When the runtime generated the row.</summary>
    public DateTime CreatedAt { get; set; }
}
