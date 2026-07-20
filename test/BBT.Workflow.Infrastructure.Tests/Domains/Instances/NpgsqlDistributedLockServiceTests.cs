using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Data;
using BBT.Workflow.Infrastructure.Execution.Locks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Integration tests for <see cref="NpgsqlDistributedLockService"/> against a real PostgreSQL,
/// using the code-first <c>MessagingDbContext</c> model (<c>sys_queues.DistributedLocks</c>) to
/// create the lease table — the same table the production EF migration
/// (AddDistributedLocksToMessagingContext) creates. Proves the service's raw-SQL operations
/// line up with the EF-owned schema and that acquire / extend / release / expired-takeover /
/// fencing behave correctly.
/// </summary>
public sealed class NpgsqlDistributedLockServiceTests : IAsyncLifetime
{
    private PostgreSqlContainer _postgresContainer = null!;
    private string _connectionString = null!;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgresContainer = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("testdb")
            .WithUsername("test")
            .WithPassword("test")
            .Build();

        await _postgresContainer.StartAsync();
        _connectionString = _postgresContainer.GetConnectionString();

        // Create the sys_queues schema + tables (incl. DistributedLocks) from the EF model —
        // the same shape the production migration produces.
        var options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        await using var context = new MessagingDbContext(options);
        await context.Database.EnsureCreatedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgresContainer.StopAsync();
        await _postgresContainer.DisposeAsync();
    }

    private NpgsqlDistributedLockService CreateService()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = _connectionString
            })
            .Build();

        return new NpgsqlDistributedLockService(configuration, NullLogger<NpgsqlDistributedLockService>.Instance);
    }

    [Fact]
    public async Task Acquire_Then_Second_Acquire_While_Held_Should_Fail()
    {
        var service = CreateService();
        var key = $"lock:{Guid.NewGuid():N}";

        await using var first = await service.TryAcquireLockAsync(key, expiryInSeconds: 60);
        first.ShouldNotBeNull();

        var second = await service.TryAcquireLockAsync(key, expiryInSeconds: 60);
        second.ShouldBeNull();
    }

    [Fact]
    public async Task Extend_By_Holder_Should_Succeed_And_Release_Frees_The_Lock()
    {
        var service = CreateService();
        var key = $"lock:{Guid.NewGuid():N}";

        var handle = await service.TryAcquireLockAsync(key, expiryInSeconds: 60);
        handle.ShouldNotBeNull();

        (await handle!.ExtendAsync(60)).ShouldBeTrue();

        await handle.ReleaseAsync();

        // After release another owner can acquire.
        await using var reacquired = await service.TryAcquireLockAsync(key, expiryInSeconds: 60);
        reacquired.ShouldNotBeNull();
    }

    [Fact]
    public async Task Expired_Lease_Should_Be_Taken_Over_With_Incremented_Fence()
    {
        var service = CreateService();
        var key = $"lock:{Guid.NewGuid():N}";

        // Acquire with a 1-second lease and let it lapse without releasing.
        var first = await service.TryAcquireLockAsync(key, expiryInSeconds: 1);
        first.ShouldNotBeNull();
        var firstFence = ((NpgsqlDistributedLockHandle)first!).Fence;

        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        // A new owner takes over the expired lease; fence advances so a stale holder is detectable.
        await using var takeover = await service.TryAcquireLockAsync(key, expiryInSeconds: 60);
        takeover.ShouldNotBeNull();
        ((NpgsqlDistributedLockHandle)takeover!).Fence.ShouldBeGreaterThan(firstFence);
    }

    [Fact]
    public async Task Extend_After_Lease_Lost_To_Another_Owner_Should_Fail()
    {
        var service = CreateService();
        var key = $"lock:{Guid.NewGuid():N}";

        var original = await service.TryAcquireLockAsync(key, expiryInSeconds: 1);
        original.ShouldNotBeNull();

        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        // Another owner takes over the expired lease.
        await using var takeover = await service.TryAcquireLockAsync(key, expiryInSeconds: 60);
        takeover.ShouldNotBeNull();

        // The original holder can no longer extend — it lost the lease.
        (await original!.ExtendAsync(60)).ShouldBeFalse();
    }
}
