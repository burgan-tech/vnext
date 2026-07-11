using BBT.Aether.DistributedLock;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BBT.Workflow.Infrastructure.Execution.Locks;

/// <summary>
/// Platform-owned distributed lock lease store on PostgreSQL — the fourth instance of the
/// proven Npgsql lease pattern (outbox / inbox / job-arming). Unlike the Dapr lock building
/// block (whose Redis component uses <c>SET NX</c> and cannot extend a held lock), this store
/// provides an ATOMIC, GAP-FREE extension (<c>UPDATE … WHERE owner AND not expired</c>) and a
/// monotonic <c>Fence</c> counter incremented on every ownership change, so downstream writers
/// can reject stale holders. Postgres is the platform's portable core per domain runtime, so
/// this keeps the SaaS infrastructure-isolation guarantee intact.
/// <para>
/// The lease table is owned code-first by <c>MessagingDbContext</c> (<c>sys_queues</c> schema,
/// see <see cref="DistributedLockRecord"/>) and created by an EF Core migration applied at
/// deploy time — consistent with the outbox/inbox/background-job tables, not runtime DDL.
/// </para>
/// <para>
/// Every operation runs on its own pooled connection, deliberately OUTSIDE any ambient
/// Unit-of-Work transaction: a lock's lifetime is independent of business transactions, and a
/// rolled-back transaction must never un-acquire a lock the caller believes it holds.
/// </para>
/// </summary>
public sealed class NpgsqlDistributedLockService : IDistributedLockService
{
    // Schema-qualified table owned code-first by MessagingDbContext (sys_queues) and created by
    // an EF Core migration at deploy time — see DistributedLockRecord. The service only reads/
    // writes it at runtime with raw ADO.NET.
    private const string Schema = "sys_queues";
    private const string Table = "DistributedLocks";

    private readonly string _connectionString;
    private readonly ILogger<NpgsqlDistributedLockService> _logger;

    public NpgsqlDistributedLockService(
        IConfiguration configuration,
        ILogger<NpgsqlDistributedLockService> logger)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Default is required for the Postgres lock provider.");
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IDistributedLockHandle?> TryAcquireLockAsync(
        string resourceId, int expiryInSeconds = 60, CancellationToken cancellationToken = default)
    {
        var owner = GenerateUniqueOwner();

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Single atomic statement: insert a fresh lease, or take over an expired one (or
        // refresh a lease this owner already holds). The fence increments on every ownership
        // change so a stale holder can be told apart from the current one.
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {Schema}."{Table}" AS l ("Key", "Owner", "Fence", "ExpiresAt")
            VALUES (@key, @owner, 1, now() + make_interval(secs => @ttl))
            ON CONFLICT ("Key") DO UPDATE
            SET "Owner"     = EXCLUDED."Owner",
                "Fence"     = l."Fence" + 1,
                "ExpiresAt" = EXCLUDED."ExpiresAt"
            WHERE l."ExpiresAt" <= now() OR l."Owner" = EXCLUDED."Owner"
            RETURNING "Fence";
            """, connection);
        command.Parameters.AddWithValue("key", resourceId);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("ttl", (double)expiryInSeconds);

        var fence = await command.ExecuteScalarAsync(cancellationToken);
        if (fence is null)
        {
            _logger.LogWarning("Failed to acquire Postgres lock for resource {ResourceId}", resourceId);
            return null;
        }

        _logger.LogDebug(
            "Acquired Postgres lock for resource {ResourceId} with owner {Owner} (fence={Fence}, lease={LeaseSeconds}s)",
            resourceId, owner, fence, expiryInSeconds);

        return new NpgsqlDistributedLockHandle(_connectionString, resourceId, owner, (long)fence, _logger);
    }

    /// <inheritdoc />
    [Obsolete("Use IDistributedLockHandle.ReleaseAsync() or dispose the handle returned by TryAcquireLockAsync. This method uses a static owner and cannot reliably release locks acquired concurrently.")]
    public Task<bool> ReleaseLockAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "ReleaseLockAsync(resourceId) cannot identify the lock owner and is a no-op for the " +
            "Postgres lock provider; release via the handle returned by TryAcquireLockAsync.");
        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public async Task<(bool Acquired, T? Result)> ExecuteWithLockAsync<T>(
        string resourceId, Func<Task<T>> function, int expiryInSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        await using var handle = await TryAcquireLockAsync(resourceId, expiryInSeconds, cancellationToken);
        if (handle is null)
        {
            return (false, default);
        }

        var result = await function();
        return (true, result);
    }

    /// <inheritdoc />
    public async Task<bool> ExecuteWithLockAsync(
        string resourceId, Func<Task> action, int expiryInSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        await using var handle = await TryAcquireLockAsync(resourceId, expiryInSeconds, cancellationToken);
        if (handle is null)
        {
            return false;
        }

        await action();
        return true;
    }

    private static string GenerateUniqueOwner()
        => $"{Environment.MachineName}:{Guid.NewGuid():N}";
}

/// <summary>
/// Handle for a Postgres lease acquired by <see cref="NpgsqlDistributedLockService"/>.
/// <see cref="ExtendAsync"/> is a true, gap-free extension: it only succeeds while this owner
/// still holds an unexpired lease, so a <c>false</c> reliably means the lock was lost.
/// </summary>
internal sealed class NpgsqlDistributedLockHandle(
    string connectionString,
    string lockKey,
    string owner,
    long fence,
    ILogger logger) : IDistributedLockHandle
{
    private int _disposed;

    /// <inheritdoc />
    public string LockKey => lockKey;

    /// <inheritdoc />
    public string Owner => owner;

    /// <summary>
    /// Monotonic fencing token: incremented on every ownership change of this key. Downstream
    /// writers may persist/compare it to reject a stale holder after a lost lease.
    /// </summary>
    public long Fence => fence;

    /// <inheritdoc />
    public async Task<bool> ExtendAsync(int leaseSeconds, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            UPDATE sys_queues."DistributedLocks"
            SET "ExpiresAt" = now() + make_interval(secs => @ttl)
            WHERE "Key" = @key AND "Owner" = @owner AND "ExpiresAt" > now();
            """, connection);
        command.Parameters.AddWithValue("key", lockKey);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("ttl", (double)leaseSeconds);

        var extended = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (!extended)
        {
            logger.LogWarning(
                "Failed to extend Postgres lock for resource {ResourceId} with owner {Owner}: lease lost or expired",
                lockKey, owner);
        }

        return extended;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                DELETE FROM sys_queues."DistributedLocks"
                WHERE "Key" = @key AND "Owner" = @owner;
                """, connection);
            command.Parameters.AddWithValue("key", lockKey);
            command.Parameters.AddWithValue("owner", owner);
            await command.ExecuteNonQueryAsync(cancellationToken);

            logger.LogDebug("Released Postgres lock for resource {ResourceId} with owner {Owner}",
                lockKey, owner);
        }
        catch (Exception ex)
        {
            // Best-effort release: an unreleased lease self-expires at ExpiresAt.
            logger.LogError(ex, "Error releasing Postgres lock for resource {ResourceId} with owner {Owner}",
                lockKey, owner);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(ReleaseAsync());
}
