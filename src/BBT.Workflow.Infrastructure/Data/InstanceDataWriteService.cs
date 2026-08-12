using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BBT.Workflow.Data;

/// <summary>
/// Explicit InstanceData persist path (architecture decision: no DbContext-level interception).
/// Serializes concurrent InstanceData writers with a per-instance PostgreSQL <c>FOR UPDATE</c>
/// row lock and assigns the version identity under that lock, then owns the SaveChanges. Call
/// sites invoke it at the exact point they previously saved with <c>autoSave: true</c>; the
/// guard in <see cref="WorkflowDbContext"/> catches any path that forgets.
/// <para>
/// Flow — inside the ambient transaction when one is open, otherwise inside a local one:
/// <c>SET LOCAL</c> lock/statement timeouts → lock the parent Instances row → read the
/// authoritative head → assign monotonic <see cref="InstanceData.VersionNo"/>s (head + 1) and
/// rebase stale semantic versions onto the real head → demote any stale latest row → save.
/// A brand-new instance has no row to lock yet — harmless: it cannot have a concurrent writer,
/// the head read returns empty and numbering starts at 1. The partial unique indexes on
/// <c>InstancesData</c> remain the database-level backstop.
/// </para>
/// </summary>
public sealed class InstanceDataWriteService(
    IAetherDbContextProvider<WorkflowDbContext> dbContextProvider,
    IOptions<WorkflowExecutionOptions> executionOptions,
    ILogger<InstanceDataWriteService> logger) : IInstanceDataWriteService
{
    /// <inheritdoc />
    public async Task SaveWithVersioningAsync(
        Instance instance,
        CancellationToken cancellationToken = default)
    {
        var context = await dbContextProvider.GetDbContextAsync();

        var added = context.ChangeTracker.Entries<InstanceData>()
            .Where(e => e.State == EntityState.Added && e.Entity.InstanceId == instance.Id)
            .Select(e => e.Entity)
            .ToList();

        if (added.Count == 0)
        {
            // Nothing to version — still perform the save the call site asked for.
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        // The row lock is transaction-scoped: with an ambient UoW transaction the lock rides it
        // (held until that commit); without one, open a local transaction so lock + inserts
        // commit atomically.
        if (context.Database.CurrentTransaction is not null)
        {
            await ApplyAndSaveAsync(context, instance.Id, added, cancellationToken);
            return;
        }

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await ApplyAndSaveAsync(context, instance.Id, added, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ApplyAndSaveAsync(
        WorkflowDbContext context,
        Guid instanceId,
        List<InstanceData> rows,
        CancellationToken cancellationToken)
    {
        var options = executionOptions.Value.InstanceDataWrite;
        var schema = SanitizeIdentifier(context.CurrentSchemaName ?? "public");

        try
        {
            // Transaction-scoped timeouts (SET LOCAL — PgBouncer transaction-mode safe). The
            // statement cap applies to every statement for the remainder of this transaction;
            // it is a per-statement limit, not a whole-transaction budget.
            await context.Database.ExecuteSqlRawAsync(
                $"SET LOCAL lock_timeout = '{options.LockTimeoutMs}ms'; " +
                $"SET LOCAL statement_timeout = '{options.StatementTimeoutMs}ms';",
                cancellationToken);

            // Lock the parent Instances row — every InstanceData writer for this instance
            // serializes here until the enclosing transaction commits. A brand-new instance
            // matches no row (no lock needed — no competitor can see it yet).
            await context.Database.ExecuteSqlRawAsync(
                $"SELECT 1 FROM \"{schema}\".\"Instances\" WHERE \"Id\" = {{0}} FOR UPDATE",
                [instanceId],
                cancellationToken);

            // Authoritative head, read under the lock (index-only via the partial unique index).
            var head = await context.Database.SqlQueryRaw<InstanceDataHeadRow>(
                    $"SELECT \"VersionNo\", \"Version\", \"HistorySequence\" " +
                    $"FROM \"{schema}\".\"InstancesData\" WHERE \"InstanceId\" = {{0}} AND \"IsLatest\"",
                    instanceId)
                .FirstOrDefaultAsync(cancellationToken);

            AssignVersions(instanceId, rows, head, logger);

            // Demote any stale latest row (committed by a concurrent transaction after our
            // aggregate was loaded) before the inserts run — the tracked old head's own EF
            // update writes the same value and is harmless. Skipped when nothing new claims
            // latest (older-line appends keep the current head).
            if (rows.Any(r => r.IsLatest))
            {
                var demoted = await context.Database.ExecuteSqlRawAsync(
                    $"UPDATE \"{schema}\".\"InstancesData\" SET \"IsLatest\" = FALSE " +
                    $"WHERE \"InstanceId\" = {{0}} AND \"IsLatest\"",
                    [instanceId],
                    cancellationToken);

                if (demoted > 0)
                    logger.InstanceDataStaleLatestDemoted(instanceId, rows.Max(r => r.VersionNo));
            }

            await context.SaveChangesAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            logger.InstanceDataLockWaitTimeout(instanceId, options.LockTimeoutMs);
            throw new InstanceDataLockTimeoutException(instanceId);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.QueryCanceled)
        {
            logger.InstanceDataWriteStatementTimeout(instanceId, options.StatementTimeoutMs);
            throw new InstanceDataWriteTimeoutException(instanceId);
        }
    }

    /// <summary>
    /// Pure core (testable without a database): assigns sequential VersionNos from the
    /// authoritative head and rebases stale semantic versions onto it. Rows are processed in
    /// their in-memory version order; each processed row becomes the effective head for the
    /// next, so multi-row saves chain correctly.
    /// <para>
    /// The loop is NOT a defensive leftover — multi-row saves are real: a task step applies all
    /// of its tasks' snapshot outputs in one save (several OnExecute tasks, parallel-branch
    /// merges), so one <c>SaveWithVersioningAsync</c> can carry several Added rows. The dominant
    /// single-row case costs one iteration; do not replace this with head-plus-one arithmetic.
    /// </para>
    /// </summary>
    internal static void AssignVersions(
        Guid instanceId,
        List<InstanceData> rows,
        InstanceDataHeadRow? head,
        ILogger logger)
    {
        var nextVersionNo = head?.VersionNo ?? 0L;
        var effectiveHeadVersion = head?.Version;
        var effectiveHeadHistorySequence = head?.HistorySequence ?? 0;

        rows.Sort(InstanceDataVersionComparer.Instance);

        foreach (var row in rows)
        {
            // Stale base: the row's computed version does not sit above the real head — a
            // concurrent writer committed in between. Re-apply the row's own strategy to the
            // real head (no-op for first-row / explicit-version appends, which keep their
            // authored version and are separated by VersionNo alone).
            if (effectiveHeadVersion is not null
                && InstanceDataVersionComparer.CompareVersionStrings(row.Version, effectiveHeadVersion) <= 0)
            {
                var staleVersion = row.Version;
                row.RebaseVersion(effectiveHeadVersion, effectiveHeadHistorySequence);

                if (!string.Equals(staleVersion, row.Version, StringComparison.Ordinal))
                    logger.InstanceDataVersionRebased(instanceId, staleVersion, row.Version, nextVersionNo + 1);
            }

            row.VersionNo = ++nextVersionNo;

            if (row.IsLatest)
            {
                effectiveHeadVersion = row.Version;
                effectiveHeadHistorySequence = row.HistorySequence;
            }
        }
    }

    private static string SanitizeIdentifier(string identifier)
        => identifier.Replace("\"", "", StringComparison.Ordinal);
}

/// <summary>
/// Projection of the current latest InstanceData row, read under the FOR UPDATE lock.
/// Property names match the quoted column names in the raw query.
/// </summary>
internal sealed class InstanceDataHeadRow
{
    public long VersionNo { get; set; }
    public string Version { get; set; } = string.Empty;
    public int HistorySequence { get; set; }
}
