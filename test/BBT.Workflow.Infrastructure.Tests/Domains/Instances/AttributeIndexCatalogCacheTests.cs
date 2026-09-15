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

    private PostgresAttributeIndexService Service(AttributeIndexOptions? settings = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = Connection
        }).Build();
        var options = Substitute.For<IOptionsMonitor<AttributeIndexOptions>>();
        options.CurrentValue.Returns(settings ?? new AttributeIndexOptions { Enabled = true });
        return new PostgresAttributeIndexService(configuration, options, Substitute.For<ICurrentSchema>(),
            new DefaultSchemaNameFormatter(), cache, locks);
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
