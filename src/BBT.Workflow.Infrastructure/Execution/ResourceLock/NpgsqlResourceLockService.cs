using BBT.Workflow.Execution;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BBT.Workflow.Infrastructure.Execution.ResourceLock;

/// <summary>
/// PostgreSQL lease implementation of <see cref="IResourceLockService"/>, for workflows that need a
/// lock they can actually RENEW.
///
/// <para>
/// The Dapr implementation cannot: its Redis component uses <c>SET NX</c>, which rejects a re-acquire
/// even from the owner, so <see cref="DaprResourceLockService.ExtendAsync"/> fails while the lock is
/// held and only "succeeds" once the TTL has already lapsed — by then it is a fresh race, not an
/// extension. A long-running protected section therefore has no way to keep its lock, and silently
/// loses it mid-flight. Here the extension is one atomic statement guarded on
/// <c>owner AND not expired</c>, so a <c>false</c> reliably means the lease is gone.
/// </para>
///
/// <para>
/// Storage is the platform's existing lease table (<c>sys_queues."DistributedLocks"</c>, owned
/// code-first by <c>MessagingDbContext</c> and created by an EF Core migration at deploy time) — no
/// new table and no runtime DDL. Workflow keys are namespaced with <see cref="KeyPrefix"/> so a
/// domain-authored key can never collide with a platform lease.
/// </para>
///
/// <para>
/// Every operation runs on its own pooled connection, deliberately OUTSIDE any ambient unit of work:
/// a lock's lifetime is independent of business transactions, and a rolled-back transaction must
/// never un-acquire a lock its caller believes it still holds.
/// </para>
/// </summary>
public sealed class NpgsqlResourceLockService(
    string connectionString,
    ILogger<NpgsqlResourceLockService> logger) : IResourceLockService
{
    /// <summary>
    /// Namespace for workflow resource locks inside the shared lease table. Platform leases are keyed
    /// by internal resource ids and never carry this prefix, so the two cannot meet.
    /// </summary>
    private const string KeyPrefix = "wfres:";

    /// <inheritdoc />
    /// <remarks>
    /// One atomic statement: insert a fresh lease, take over an expired one, or refresh the lease this
    /// owner already holds. The row that wins is returned, so the caller learns whether it owns the
    /// lock without a second read — and without the read-then-write gap that would let two owners both
    /// conclude they won.
    /// </remarks>
    public async Task<bool> AcquireAsync(
        string resourceKey, string owner, int ttlSeconds, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand("""
            INSERT INTO sys_queues."DistributedLocks" AS l ("Key", "Owner", "Fence", "ExpiresAt")
            VALUES (@key, @owner, 1, now() + make_interval(secs => @ttl))
            ON CONFLICT ("Key") DO UPDATE
            SET "Owner"     = EXCLUDED."Owner",
                "Fence"     = l."Fence" + 1,
                "ExpiresAt" = EXCLUDED."ExpiresAt"
            WHERE l."ExpiresAt" <= now() OR l."Owner" = EXCLUDED."Owner"
            RETURNING "Owner";
            """, connection);
        command.Parameters.AddWithValue("key", Prefixed(resourceKey));
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("ttl", (double)ttlSeconds);

        // No row back means the ON CONFLICT guard rejected the write: a live lease owned by somebody else.
        var acquired = await command.ExecuteScalarAsync(cancellationToken) is string;

        if (acquired)
        {
            logger.ResourceLockAcquired(resourceKey, owner, ttlSeconds);
            return true;
        }

        logger.ResourceLockAcquireConflict(resourceKey, owner);
        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Idempotent, like DELETE: the postcondition is "this owner no longer holds the lock". A lease
    /// that is already gone (released, or expired) satisfies it and reports success. Only finding the
    /// key held by a DIFFERENT, still-live owner is an anomaly — logged, never thrown, because release
    /// runs after the business transition already succeeded.
    /// </remarks>
    public async Task<bool> ReleaseAsync(
        string resourceKey, string owner, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // The snapshot CTE reads the pre-delete state, so one round trip tells the three cases apart:
        // released by us, already gone, or held by someone else.
        await using var command = new NpgsqlCommand("""
            WITH snapshot AS (
                SELECT "Owner", "ExpiresAt" FROM sys_queues."DistributedLocks" WHERE "Key" = @key
            ), removed AS (
                DELETE FROM sys_queues."DistributedLocks" WHERE "Key" = @key AND "Owner" = @owner
                RETURNING 1
            )
            SELECT EXISTS (SELECT 1 FROM removed) AS released,
                   EXISTS (SELECT 1 FROM snapshot WHERE "ExpiresAt" > now()) AS held_by_live_lease;
            """, connection);
        command.Parameters.AddWithValue("key", Prefixed(resourceKey));
        command.Parameters.AddWithValue("owner", owner);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var released = false;
        var heldByLiveLease = false;
        if (await reader.ReadAsync(cancellationToken))
        {
            released = reader.GetBoolean(0);
            heldByLiveLease = reader.GetBoolean(1);
        }

        if (released)
        {
            logger.ResourceLockReleased(resourceKey, owner);
            return true;
        }

        if (!heldByLiveLease)
        {
            logger.ResourceLockReleaseNoop(resourceKey, owner);
            return true;
        }

        logger.ResourceLockReleaseFailed(resourceKey, owner, "LockBelongsToOthers");
        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A genuine, gap-free extension — the whole reason this implementation exists. The guard
    /// (<c>owner AND not expired</c>) means the lease is never resurrected after it lapsed: once it is
    /// gone, another instance may legitimately hold it, and quietly re-taking it would put two owners
    /// inside the protected section.
    /// </remarks>
    public async Task<bool> ExtendAsync(
        string resourceKey, string owner, int ttlSeconds, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand("""
            UPDATE sys_queues."DistributedLocks"
            SET "ExpiresAt" = now() + make_interval(secs => @ttl)
            WHERE "Key" = @key AND "Owner" = @owner AND "ExpiresAt" > now();
            """, connection);
        command.Parameters.AddWithValue("key", Prefixed(resourceKey));
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("ttl", (double)ttlSeconds);

        if (await command.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            logger.ResourceLockExtended(resourceKey, owner, ttlSeconds);
            return true;
        }

        logger.ResourceLockExtendFailed(resourceKey, owner);
        return false;
    }

    private static string Prefixed(string resourceKey) => KeyPrefix + resourceKey;
}
