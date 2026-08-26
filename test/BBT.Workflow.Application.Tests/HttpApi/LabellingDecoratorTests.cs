using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedCache;
using BBT.Aether.DistributedLock;
using BBT.Workflow.HttpApi.Shared.Telemetry;
using BBT.Workflow.Logging;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HttpApi;

/// <summary>
/// Pins the labelling decorators around the Dapr-backed cache/lock services: the
/// <see cref="DaprCallLabel"/> ambient holds the key exactly for the duration of the inner call
/// (so the gRPC span started inside it gets tagged) and unwinds afterwards — including nested
/// scopes, where a cache read inside an ExecuteWithLock body re-labels and then restores the
/// lock's resource id for the trailing Unlock.
/// </summary>
public sealed class LabellingDecoratorTests
{
    [Fact]
    public async Task CacheGet_PublishesTheKey_OnlyForTheDurationOfTheCall()
    {
        string? observedDuringCall = null;
        var cache = new LabellingDistributedCacheService(new FakeCache(
            onCall: () => observedDuringCall = DaprCallLabel.Current));

        await cache.GetAsync<string>("flow:core:my-workflow:gen");

        observedDuringCall.ShouldBe("flow:core:my-workflow:gen");
        DaprCallLabel.Current.ShouldBeNull();
    }

    [Fact]
    public async Task CacheGetOrSet_WithKeyFactory_LabelsWithTheDerivedKey()
    {
        string? observedDuringCall = null;
        var cache = new LabellingDistributedCacheService(new FakeCache(
            onCall: () => observedDuringCall = DaprCallLabel.Current));

        await cache.GetOrSetAsync(
            request: 42,
            factory: _ => Task.FromResult("value"),
            keyFactory: id => $"answer:{id}");

        observedDuringCall.ShouldBe("answer:42");
        DaprCallLabel.Current.ShouldBeNull();
    }

    [Fact]
    public async Task LockAcquire_PublishesTheResourceId_OnlyForTheDurationOfTheCall()
    {
        string? observedDuringCall = null;
        var lockService = new LabellingDistributedLockService(new FakeLock(
            onCall: () => observedDuringCall = DaprCallLabel.Current));

        await lockService.TryAcquireLockAsync("vnext:core:my-flow:123", 5);

        observedDuringCall.ShouldBe("vnext:core:my-flow:123");
        DaprCallLabel.Current.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteWithLock_BodyCacheCall_RelabelsAndUnwindsToTheLockKey()
    {
        string? insideCacheCall = null;
        string? afterCacheCall = null;
        var cache = new LabellingDistributedCacheService(new FakeCache(
            onCall: () => insideCacheCall = DaprCallLabel.Current));
        var lockService = new LabellingDistributedLockService(new FakeLock());

        await lockService.ExecuteWithLockAsync("lock-key", async () =>
        {
            await cache.GetAsync<string>("cache-key");
            afterCacheCall = DaprCallLabel.Current;
            return "done";
        });

        insideCacheCall.ShouldBe("cache-key");
        // The cache scope unwound back to the lock's key, so the trailing Unlock is labelled.
        afterCacheCall.ShouldBe("lock-key");
        DaprCallLabel.Current.ShouldBeNull();
    }

    private sealed class FakeCache(Action? onCall = null) : IDistributedCacheService
    {
        private void Observe() => onCall?.Invoke();

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
        {
            Observe();
            return Task.FromResult<T?>(null);
        }

        public Task SetAsync<T>(string key, T value, DistributedCacheEntryOptions? options = null, CancellationToken cancellationToken = default) where T : class
        {
            Observe();
            return Task.CompletedTask;
        }

        public async Task<T?> GetOrSetAsync<T>(string key, Func<Task<T>> factory, DistributedCacheEntryOptions? options = null, CancellationToken cancellationToken = default) where T : class
        {
            Observe();
            return await factory();
        }

        public async Task<T?> GetOrSetAsync<TKey, T>(TKey request, Func<TKey, Task<T>> factory, Func<TKey, string>? keyFactory = null, DistributedCacheEntryOptions? options = null, CancellationToken cancellationToken = default) where T : class
        {
            Observe();
            return await factory(request);
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            Observe();
            return Task.CompletedTask;
        }

        public Task RefreshAsync(string key, CancellationToken cancellationToken = default)
        {
            Observe();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLock(Action? onCall = null) : IDistributedLockService
    {
        private void Observe() => onCall?.Invoke();

        public Task<IDistributedLockHandle?> TryAcquireLockAsync(string resourceId, int expiryInSeconds = 30, CancellationToken cancellationToken = default)
        {
            Observe();
            return Task.FromResult<IDistributedLockHandle?>(null);
        }

        public Task<bool> ReleaseLockAsync(string resourceId, CancellationToken cancellationToken = default)
        {
            Observe();
            return Task.FromResult(true);
        }

        public async Task<(bool Acquired, T? Result)> ExecuteWithLockAsync<T>(string resourceId, Func<Task<T>> function, int expiryInSeconds = 30, CancellationToken cancellationToken = default)
        {
            Observe();
            return (true, await function());
        }

        public async Task<bool> ExecuteWithLockAsync(string resourceId, Func<Task> action, int expiryInSeconds = 30, CancellationToken cancellationToken = default)
        {
            Observe();
            await action();
            return true;
        }
    }
}
