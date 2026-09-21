using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedCache;
using BBT.Aether.DistributedLock;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Schemas;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

public sealed class AttributeIndexCatalogCacheTests
{
    private const string Connection = "Host=localhost;Port=1;Database=catalog_test;Username=reader;Password=secret";
    private readonly IDistributedCacheService cache = new NetCoreDistributedCacheService(
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
    private readonly IDistributedLockService locks = Substitute.For<IDistributedLockService>();

    private static PostgresAttributeIndexService.ReadyIndexSnapshot Snapshot(params string[] keys)
        => new(keys, DateTimeOffset.UtcNow.AddMinutes(1));

    private PostgresAttributeIndexService Service(AttributeIndexOptions? settings = null,
        IDistributedCacheService? sharedCache = null, ILogger<PostgresAttributeIndexService>? logger = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = Connection
        }).Build();
        var options = Substitute.For<IOptionsMonitor<AttributeIndexOptions>>();
        options.CurrentValue.Returns(settings ?? new AttributeIndexOptions { Enabled = true });
        return new PostgresAttributeIndexService(configuration, options, Substitute.For<ICurrentSchema>(),
            new DefaultSchemaNameFormatter(), sharedCache ?? cache, locks, logger ?? NullLogger<PostgresAttributeIndexService>.Instance);
    }

    [Fact]
    public async Task ReplicasShareSnapshotAndObserveReplacementWithoutLocalCache()
    {
        var first = Service();
        var second = Service();
        var key = PostgresAttributeIndexService.BuildCacheKey(Connection, "orders_flow");
        await cache.SetAsync(key, Snapshot("numeric"));
        Assert.Contains("numeric", await first.GetReadyAsync("orders-flow"));
        Assert.Contains("numeric", await second.GetReadyAsync("orders_flow"));
        await cache.SetAsync(key, Snapshot());
        Assert.Empty(await first.GetReadyAsync("orders-flow"));
        Assert.Empty(await second.GetReadyAsync("orders_flow"));
        Assert.Empty(locks.ReceivedCalls());
    }

    [Fact]
    public async Task DisabledRoutingOverridesSharedReadySnapshot()
    {
        await cache.SetAsync(PostgresAttributeIndexService.BuildCacheKey(Connection, "orders_flow"), Snapshot("numeric"));
        Assert.Empty(await Service(new AttributeIndexOptions()).GetReadyAsync("orders_flow"));
        Assert.Empty(await Service(new AttributeIndexOptions { Enabled = true, DisabledFlows = ["orders-flow"] })
            .GetReadyAsync("orders_flow"));
        Assert.Empty(locks.ReceivedCalls());
    }

    [Fact]
    public async Task RefreshContenderFallsBackWithoutDatabaseQueryOrCachingTemporaryEmptyResult()
    {
        locks.TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IDistributedLockHandle?)null);
        var service = Service(); // No lock handle: another replica owns the refresh.
        Assert.Empty(await service.GetReadyAsync("orders"));
        var key = PostgresAttributeIndexService.BuildCacheKey(Connection, "orders");
        Assert.Null(await cache.GetAsync<PostgresAttributeIndexService.ReadyIndexSnapshot>(key));
        await cache.SetAsync(key, Snapshot("ready"));
        Assert.Contains("ready", await service.GetReadyAsync("orders"));
    }

    [Fact]
    public async Task RefreshOwnerRechecksSnapshotAfterAcquiringLock()
    {
        var key = PostgresAttributeIndexService.BuildCacheKey(Connection, "orders");
        var handle = Substitute.For<IDistributedLockHandle>();
        locks.TryAcquireLockAsync(key + ":refresh", 60, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await cache.SetAsync(key, Snapshot("ready"));
            return (IDistributedLockHandle?)handle;
        });
        Assert.Contains("ready", await Service().GetReadyAsync("orders"));
        await handle.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ExpiredSnapshotIsRejectedEvenIfProviderHasNotEvictedIt()
    {
        var key = PostgresAttributeIndexService.BuildCacheKey(Connection, "orders");
        await cache.SetAsync(key, new PostgresAttributeIndexService.ReadyIndexSnapshot(["obsolete"], DateTimeOffset.UtcNow.AddSeconds(-1)));
        locks.TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IDistributedLockHandle?)null);
        Assert.Empty(await Service().GetReadyAsync("orders"));
    }

    [Theory]
    [InlineData("cache-read")]
    [InlineData("cache-recheck")]
    [InlineData("lock-acquire")]
    [InlineData("lock-release")]
    [InlineData("dependency-cancel")]
    public async Task DependencyFailureFallsBackLogsAndDoesNotCacheEmptySnapshot(string phase)
    {
        var sharedCache = Substitute.For<IDistributedCacheService>();
        var logger = Substitute.For<ILogger<PostgresAttributeIndexService>>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);
        var failure = phase == "dependency-cancel"
            ? (Exception)new OperationCanceledException("Dependency timeout without caller cancellation")
            : new TimeoutException("Dependency unavailable");
        var handle = Substitute.For<IDistributedLockHandle>();
        locks.TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => phase == "lock-acquire" ? throw failure : handle);
        var reads = 0;
        sharedCache.GetAsync<PostgresAttributeIndexService.ReadyIndexSnapshot>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                reads++;
                if (phase is "cache-read" or "dependency-cancel" || phase == "cache-recheck" && reads == 2)
                    throw failure;
                return Task.FromResult(phase == "lock-release" && reads == 2 ? Snapshot("ready") : null);
            });
        if (phase == "lock-release")
            handle.DisposeAsync().Returns(_ => ValueTask.FromException(failure));

        Assert.Empty(await Service(sharedCache: sharedCache, logger: logger).GetReadyAsync("orders"));
        Assert.DoesNotContain(sharedCache.ReceivedCalls(), call => call.GetMethodInfo().Name == "SetAsync");
        var warning = Assert.Single(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == "Log");
        Assert.Equal(LogLevel.Warning, warning.GetArguments()[0]);
        Assert.Equal(70021, ((EventId)warning.GetArguments()[1]!).Id);
        Assert.Same(failure, warning.GetArguments()[3]);
        if (phase is "cache-recheck" or "lock-release")
            await handle.Received(1).DisposeAsync();
    }

    [Fact]
    public async Task ConnectionOpenFailureFallsBackWithoutPoisoningCacheAndReleasesLock()
    {
        var handle = Substitute.For<IDistributedLockHandle>();
        locks.TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(handle);
        var service = Service(); // Port 1: connection open fails before the catalog command.
        Assert.Empty(await service.GetReadyAsync("orders"));
        var key = PostgresAttributeIndexService.BuildCacheKey(Connection, "orders");
        Assert.Null(await cache.GetAsync<PostgresAttributeIndexService.ReadyIndexSnapshot>(key));
        await handle.Received(1).DisposeAsync();
        await cache.SetAsync(key, Snapshot("recovered"));
        Assert.Contains("recovered", await service.GetReadyAsync("orders"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerCancellationIsNotConvertedToFallback(bool cancelBeforeLookup)
    {
        using var cancellation = new CancellationTokenSource();
        var sharedCache = Substitute.For<IDistributedCacheService>();
        var logger = Substitute.For<ILogger<PostgresAttributeIndexService>>();
        logger.IsEnabled(LogLevel.Warning).Returns(true);
        sharedCache.GetAsync<PostgresAttributeIndexService.ReadyIndexSnapshot>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<PostgresAttributeIndexService.ReadyIndexSnapshot?>(cancellation.Token);
            });
        if (cancelBeforeLookup) cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(sharedCache: sharedCache, logger: logger).GetReadyAsync("orders", cancellation.Token));
        Assert.DoesNotContain(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == "Log");
        Assert.Empty(locks.ReceivedCalls());
    }

    [Fact]
    public void CacheIdentitySeparatesDatabaseRoleAndSchemaWithoutCredentials()
    {
        var key = PostgresAttributeIndexService.BuildCacheKey(Connection, "orders");
        Assert.DoesNotContain("secret", key);
        Assert.DoesNotContain("reader", key);
        Assert.Equal(key, PostgresAttributeIndexService.BuildCacheKey(Connection.Replace("secret", "rotated"), "orders"));
        Assert.NotEqual(key, PostgresAttributeIndexService.BuildCacheKey(Connection.Replace("catalog_test", "other"), "orders"));
        Assert.NotEqual(key, PostgresAttributeIndexService.BuildCacheKey(Connection.Replace("reader", "other"), "orders"));
        Assert.NotEqual(key, PostgresAttributeIndexService.BuildCacheKey(Connection, "other"));
    }
}
