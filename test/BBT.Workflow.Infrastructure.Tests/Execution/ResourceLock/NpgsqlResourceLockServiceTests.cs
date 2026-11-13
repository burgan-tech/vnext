using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Infrastructure.Execution.ResourceLock;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Execution.ResourceLock;

/// <summary>
/// Pins <see cref="NpgsqlResourceLockService"/> against a real PostgreSQL. The behaviour that matters
/// is the one the Dapr implementation cannot give: an extension that is atomic and refuses once the
/// lease has lapsed. Mutual exclusion and idempotent release are pinned alongside it, because an
/// extendable lock that lets two owners in would be worse than no lock at all.
/// </summary>
public sealed class NpgsqlResourceLockServiceTests : IAsyncLifetime
{
    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;
    private NpgsqlResourceLockService _locks = null!;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("testdb").WithUsername("test").WithPassword("test")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        // The real table is owned code-first by MessagingDbContext and created by an EF migration at
        // deploy time; here only its shape matters, so it is created directly.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var create = new NpgsqlCommand("""
            CREATE SCHEMA IF NOT EXISTS sys_queues;
            CREATE TABLE IF NOT EXISTS sys_queues."DistributedLocks" (
                "Key"       varchar(512) PRIMARY KEY,
                "Owner"     varchar(256) NOT NULL,
                "Fence"     bigint       NOT NULL,
                "ExpiresAt" timestamptz  NOT NULL
            );
            """, connection);
        await create.ExecuteNonQueryAsync();

        _locks = new NpgsqlResourceLockService(
            _connectionString, NullLogger<NpgsqlResourceLockService>.Instance);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.StopAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Acquire_OnAFreeKey_Succeeds()
    {
        (await _locks.AcquireAsync(NewKey(), "owner-a", 60, CancellationToken.None)).ShouldBeTrue();
    }

    /// <summary>The point of the lock: a second owner is kept out while the lease is live.</summary>
    [Fact]
    public async Task Acquire_WhileAnotherOwnerHoldsALiveLease_Fails()
    {
        var key = NewKey();
        await _locks.AcquireAsync(key, "owner-a", 60, CancellationToken.None);

        (await _locks.AcquireAsync(key, "owner-b", 60, CancellationToken.None)).ShouldBeFalse();
    }

    /// <summary>Re-acquiring your own lease refreshes it instead of locking you out of your own key.</summary>
    [Fact]
    public async Task Acquire_ByTheSameOwner_Succeeds()
    {
        var key = NewKey();
        await _locks.AcquireAsync(key, "owner-a", 60, CancellationToken.None);

        (await _locks.AcquireAsync(key, "owner-a", 60, CancellationToken.None)).ShouldBeTrue();
    }

    /// <summary>An abandoned lease must not block the key forever — the TTL is the safety net.</summary>
    [Fact]
    public async Task Acquire_AfterTheLeaseExpired_IsTakenOverByAnotherOwner()
    {
        var key = NewKey();
        await _locks.AcquireAsync(key, "owner-a", 60, CancellationToken.None);
        await ExpireAsync(key);

        (await _locks.AcquireAsync(key, "owner-b", 60, CancellationToken.None)).ShouldBeTrue();
    }

    /// <summary>
    /// The capability the Dapr store lacks: the holder renews a live lease, and the renewal actually
    /// moves the expiry — so a long protected section can keep its lock instead of losing it silently.
    /// </summary>
    [Fact]
    public async Task Extend_ByTheHolder_RenewsTheLiveLease()
    {
        var key = NewKey();
        await _locks.AcquireAsync(key, "owner-a", 2, CancellationToken.None);
        var before = await ReadExpiryAsync(key);

        (await _locks.ExtendAsync(key, "owner-a", 600, CancellationToken.None)).ShouldBeTrue();

        (await ReadExpiryAsync(key)).ShouldBeGreaterThan(before);
        // Still exclusive after the extension.
        (await _locks.AcquireAsync(key, "owner-b", 60, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Extend_ByAnotherOwner_Fails()
    {
        var key = NewKey();
        await _locks.AcquireAsync(key, "owner-a", 60, CancellationToken.None);

        (await _locks.ExtendAsync(key, "owner-b", 600, CancellationToken.None)).ShouldBeFalse();
    }

    /// <summary>
    /// Once the lease has lapsed another instance may legitimately hold the key, so a late extension
    /// must refuse rather than quietly resurrect it — that would put two owners inside the section.
    /// </summary>
    [Fact]
    public async Task Extend_AfterTheLeaseExpired_Fails()
    {
        var key = NewKey();
        await _locks.AcquireAsync(key, "owner-a", 60, CancellationToken.None);
        await ExpireAsync(key);

        (await _locks.ExtendAsync(key, "owner-a", 600, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Extend_OnAKeyThatWasNeverLocked_Fails()
    {
        (await _locks.ExtendAsync(NewKey(), "owner-a", 60, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Release_ByTheHolder_FreesTheKey()
    {
        var key = NewKey();
        await _locks.AcquireAsync(key, "owner-a", 60, CancellationToken.None);

        (await _locks.ReleaseAsync(key, "owner-a", CancellationToken.None)).ShouldBeTrue();
        (await _locks.AcquireAsync(key, "owner-b", 60, CancellationToken.None)).ShouldBeTrue();
    }

    /// <summary>Release is idempotent: releasing twice, or a lease that already expired, is a no-op.</summary>
    [Fact]
    public async Task Release_WhenNothingIsHeld_ReportsSuccess()
    {
        var key = NewKey();

        (await _locks.ReleaseAsync(key, "owner-a", CancellationToken.None)).ShouldBeTrue();

        await _locks.AcquireAsync(key, "owner-a", 60, CancellationToken.None);
        await _locks.ReleaseAsync(key, "owner-a", CancellationToken.None);
        (await _locks.ReleaseAsync(key, "owner-a", CancellationToken.None)).ShouldBeTrue();
    }

    /// <summary>
    /// Releasing somebody else's live lease is refused — and, more importantly, leaves it standing.
    /// </summary>
    [Fact]
    public async Task Release_ByAnotherOwner_IsRefusedAndLeavesTheLeaseInPlace()
    {
        var key = NewKey();
        await _locks.AcquireAsync(key, "owner-a", 60, CancellationToken.None);

        (await _locks.ReleaseAsync(key, "owner-b", CancellationToken.None)).ShouldBeFalse();
        (await _locks.AcquireAsync(key, "owner-c", 60, CancellationToken.None)).ShouldBeFalse();
    }

    /// <summary>
    /// Workflow keys are namespaced inside the shared lease table, so a domain-authored key can never
    /// collide with a platform lease that happens to use the same words.
    /// </summary>
    [Fact]
    public async Task Acquire_NamespacesTheKeyAwayFromPlatformLeases()
    {
        var key = NewKey();
        await _locks.AcquireAsync(key, "owner-a", 60, CancellationToken.None);

        (await ReadOwnerAsync(key)).ShouldBe("owner-a");
        (await ReadRawOwnerAsync(key)).ShouldBeNull();
    }

    private static string NewKey() => $"txnlimit:{Guid.NewGuid():N}";

    private async Task ExpireAsync(string resourceKey)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE sys_queues."DistributedLocks" SET "ExpiresAt" = now() - interval '1 second'
            WHERE "Key" = @key;
            """, connection);
        command.Parameters.AddWithValue("key", "wfres:" + resourceKey);
        (await command.ExecuteNonQueryAsync()).ShouldBe(1);
    }

    private Task<DateTime> ReadExpiryAsync(string resourceKey) =>
        ScalarAsync<DateTime>("""SELECT "ExpiresAt" FROM sys_queues."DistributedLocks" WHERE "Key" = @key;""",
            "wfres:" + resourceKey);

    private Task<string?> ReadOwnerAsync(string resourceKey) =>
        ScalarAsync<string?>("""SELECT "Owner" FROM sys_queues."DistributedLocks" WHERE "Key" = @key;""",
            "wfres:" + resourceKey);

    /// <summary>The unprefixed key — what a platform lease would be stored under.</summary>
    private Task<string?> ReadRawOwnerAsync(string resourceKey) =>
        ScalarAsync<string?>("""SELECT "Owner" FROM sys_queues."DistributedLocks" WHERE "Key" = @key;""",
            resourceKey);

    private async Task<T?> ScalarAsync<T>(string sql, string key)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("key", key);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }
}
