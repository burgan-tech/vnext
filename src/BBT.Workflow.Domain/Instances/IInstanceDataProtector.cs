namespace BBT.Workflow.Instances;

/// <summary>
/// Opens the <c>x-encryption.type: "encrypt"</c> tokens of a stored <see cref="InstanceData"/> row.
/// <para>
/// Implemented in Infrastructure (the only layer that reads the per-instance secrets) and handed to each materialized row
/// by an EF materialization interceptor, together with the flow schema the row was read from; the row decrypts lazily on
/// its first <see cref="InstanceData.Data"/> read. Decryption is driven by the token prefix, never by the current schema,
/// so a schema version that stopped encrypting a field still reads the rows written while it did.
/// </para>
/// </summary>
public interface IInstanceDataProtector
{
    /// <summary>
    /// Returns the plaintext view of <paramref name="stored"/>. Never throws for a bad token: a token that cannot be
    /// opened (no secret for the instance, failed authentication) is reported in
    /// <see cref="InstanceDataView.Undecryptable"/> and left in place.
    /// </summary>
    InstanceDataView Unprotect(string? schema, Guid instanceId, JsonData stored);
}

/// <summary>
/// Loads the per-instance <c>x-encryption</c> secrets of the given instances into the in-process cache, in one query, so
/// that opening their rows afterwards needs no database round trip. Scoped: it reads through the current flow schema's
/// context. Call it at the entry points that are about to read instance data; a row opened without a preload still works
/// (a slower synchronous lookup), and an instance without a secret simply has nothing to open.
/// </summary>
public interface IInstanceSecretPreloader
{
    /// <summary>Preloads the secrets of <paramref name="instanceIds"/> that are not cached yet.</summary>
    Task PreloadAsync(IReadOnlyCollection<Guid> instanceIds, CancellationToken cancellationToken = default);
}
