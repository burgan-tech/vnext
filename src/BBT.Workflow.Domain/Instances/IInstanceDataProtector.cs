namespace BBT.Workflow.Instances;

/// <summary>
/// Opens the <c>x-encryption.type: "encrypt"</c> tokens of a stored <see cref="InstanceData"/> row — on demand only.
/// <para>
/// Implemented in Infrastructure (the only layer that reads the per-instance secrets). A row is never decrypted in place:
/// <see cref="InstanceData.Data"/> is always the stored form. The callers that need plaintext ask for it here — the write
/// funnel (to merge and validate), the read guard (for a caller exempt from an <c>encrypt</c> rule) and a script's
/// <see cref="Instance.DecryptAsync"/>. Decryption is driven by the token prefix, never by the current schema, so a schema
/// version that stopped encrypting a field still opens the rows written while it did.
/// </para>
/// </summary>
public interface IInstanceDataProtector
{
    /// <summary>
    /// Returns the plaintext view of <paramref name="stored"/>. When the instance's secret is not cached it is loaded first,
    /// through EF on a context of its own — never the caller's DbContext, which a parallel task branch may be using. Never
    /// throws for a bad token: a token that cannot be opened (no secret for the instance, failed authentication) is reported
    /// in <see cref="InstanceDataView.Undecryptable"/> and left in place. <paramref name="cancellationToken"/> is used for this
    /// call only; a cancelled token throws <see cref="OperationCanceledException"/> and caches nothing.
    /// </summary>
    Task<InstanceDataView> UnprotectAsync(
        string? schema, Guid instanceId, JsonData stored, CancellationToken cancellationToken = default);
}

/// <summary>
/// Loads the per-instance <c>x-encryption</c> secrets of the given instances into the in-process cache, in one query, so
/// that opening their rows afterwards needs no database round trip. Scoped: it reads through the current flow schema's
/// context. Call it at the entry points that are about to open many rows; a row opened without a preload still works
/// (one query of its own), and an instance without a secret simply has nothing to open.
/// </summary>
public interface IInstanceSecretPreloader
{
    /// <summary>Preloads the secrets of <paramref name="instanceIds"/> that are not cached yet.</summary>
    Task PreloadAsync(IReadOnlyCollection<Guid> instanceIds, CancellationToken cancellationToken = default);
}
