namespace BBT.Workflow.Security;

/// <summary>
/// What to do in one maintenance pass over the current schema's instance data.
/// </summary>
/// <param name="DryRun">
/// When true (the default) nothing is written — the pass only reports what it would change.
/// Deliberately the default: this operation rewrites the sensitive columns of live rows.
/// </param>
/// <param name="BatchSize">How many instances to load per page.</param>
/// <param name="MaxInstances">Stop after this many instances. Null means the whole schema.</param>
/// <param name="InstanceKey">Restrict the pass to a single instance key. Useful for verification.</param>
public sealed record EncryptionMaintenanceRequest(
    bool DryRun = true,
    int BatchSize = 200,
    int? MaxInstances = null,
    string? InstanceKey = null);

/// <summary>
/// Outcome of a maintenance pass.
/// </summary>
/// <param name="DryRun">Whether the pass was a simulation.</param>
/// <param name="ActiveKeyId">The key rows were (or would be) brought onto.</param>
/// <param name="InstancesScanned">Instances examined.</param>
/// <param name="RowsScanned">Instance-data rows examined.</param>
/// <param name="RowsRewritten">Rows whose stored payload changed (or would change).</param>
/// <param name="RowsAlreadyCurrent">Rows already encrypted under the active key.</param>
/// <param name="ExpiredRetentionValues">
/// Sensitive values whose <c>retentionDays</c> has elapsed. <b>Reported only, never deleted</b> —
/// see the remarks on <see cref="IInstanceDataEncryptionMaintenanceService"/>.
/// </param>
/// <param name="Failures">Per-instance failures. The pass continues past them.</param>
public sealed record EncryptionMaintenanceReport(
    bool DryRun,
    string? ActiveKeyId,
    int InstancesScanned,
    int RowsScanned,
    int RowsRewritten,
    int RowsAlreadyCurrent,
    int ExpiredRetentionValues,
    IReadOnlyList<string> Failures);

/// <summary>
/// Brings existing instance-data rows onto the active encryption key.
/// <para>
/// <b>Backfill and rotation are the same operation.</b> Both mean "make this row's encrypted-field
/// set match the active key" — backfill starts from plaintext, rotation starts from an older key,
/// and the code path is identical. Rotation alone is schema-free (the marker says what is
/// encrypted); only backfill needs the master schema, to learn what <i>ought</i> to be.
/// </para>
/// <para>
/// The pass rewrites the stored payload <b>in place</b> and nothing else. Re-encryption does not
/// change plaintext, so <c>DataHash</c>, <c>ETag</c>, <c>Version</c>, <c>VersionNo</c> and
/// <c>IsLatest</c> all stay valid: no version bump, no ETag churn, no cache invalidation, and no
/// long-polling client is disturbed. That is also what makes the pass idempotent and resumable —
/// re-running it is free because an already-current row is recognised and skipped.
/// </para>
/// <para>
/// <b>Retention is reported, not enforced.</b> Deleting or tombstoning expired values is a product
/// decision (drop the whole history row, or blank the field and accept a broken content hash?) with
/// irreversible consequences, so this service only counts them.
/// </para>
/// </summary>
public interface IInstanceDataEncryptionMaintenanceService
{
    /// <summary>
    /// Runs one maintenance pass over the current schema.
    /// </summary>
    /// <param name="request">What to scan and whether to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was found and what changed.</returns>
    Task<EncryptionMaintenanceReport> ReEncryptAsync(
        EncryptionMaintenanceRequest request,
        CancellationToken cancellationToken = default);
}
