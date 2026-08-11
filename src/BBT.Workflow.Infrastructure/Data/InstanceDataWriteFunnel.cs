using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BBT.Workflow.Data;

/// <summary>
/// Serializes InstanceData writes with a per-instance PostgreSQL <c>FOR UPDATE</c> row lock,
/// replacing the former BEFORE INSERT versioning trigger. Runs inside
/// <see cref="WorkflowDbContext"/>'s SaveChanges pipeline whenever the change tracker holds new
/// <see cref="InstanceData"/> rows, so every write path (start, pipeline steps, updateData,
/// subflow output mapping, definition seeds) funnels through it with zero call-site changes.
/// <para>
/// Under the lock — inside the same transaction the rows commit in — the funnel:
/// assigns the monotonic <see cref="InstanceData.VersionNo"/> (head + 1), rebases the semantic
/// version onto the real database head when the in-memory base was stale, and demotes any
/// stale latest row so the partial unique index <c>UX_InstancesData_Instance_IsLatest</c> holds.
/// The unique indexes remain the database-level backstop for both invariants.
/// </para>
/// </summary>
internal static class InstanceDataWriteFunnel
{
    /// <summary>
    /// Returns whether the change tracker holds newly added InstanceData rows — the only case
    /// the funnel (and its transaction requirement) applies to.
    /// </summary>
    internal static bool HasPendingInstanceData(WorkflowDbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries<InstanceData>())
        {
            if (entry.State == EntityState.Added)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Applies the funnel. Must be called inside an open transaction (the row lock is
    /// transaction-scoped) and immediately before the base SaveChanges executes the inserts.
    /// </summary>
    internal static async Task ApplyAsync(
        WorkflowDbContext context,
        InstanceDataWriteOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var added = context.ChangeTracker.Entries<InstanceData>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .ToList();

        if (added.Count == 0)
            return;

        var schema = SanitizeIdentifier(context.CurrentSchemaName ?? "public");

        // Transaction-scoped timeouts (SET LOCAL — PgBouncer transaction-mode safe). The
        // statement cap applies to every statement for the remainder of this transaction;
        // it is a per-statement limit, not a whole-transaction budget.
        await context.Database.ExecuteSqlRawAsync(
            $"SET LOCAL lock_timeout = '{options.LockTimeoutMs}ms'; " +
            $"SET LOCAL statement_timeout = '{options.StatementTimeoutMs}ms';",
            cancellationToken);

        // Deterministic instance order prevents lock-order deadlocks when a single SaveChanges
        // ever carries rows for multiple instances.
        foreach (var group in added.GroupBy(d => d.InstanceId).OrderBy(g => g.Key))
        {
            await ApplyForInstanceAsync(context, schema, group.Key, [.. group], options, logger, cancellationToken);
        }
    }

    private static async Task ApplyForInstanceAsync(
        WorkflowDbContext context,
        string schema,
        Guid instanceId,
        List<InstanceData> rows,
        InstanceDataWriteOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            // POC parity: lock the parent Instances row — every InstanceData writer for this
            // instance serializes here until the enclosing transaction commits.
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
