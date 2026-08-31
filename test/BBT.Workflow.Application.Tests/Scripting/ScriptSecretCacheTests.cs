using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Scripting.Functions;
using Dapr.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Scripting;

/// <summary>
/// Unit tests for <see cref="ScriptSecretCache"/>: bundle-level caching, TTL expiry,
/// single-flight stampede protection, no negative caching, and bypass semantics.
/// Pure instance-level tests with a fake <see cref="TimeProvider"/> — no shared process
/// state, so the ScriptingTests collection is not required.
/// </summary>
public sealed class ScriptSecretCacheTests
{
    private const string Store = "vault";
    private const string Bundle = "app-secrets";

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static Mock<DaprClient> CreateDaprMock(Dictionary<string, string> bundle)
    {
        var mock = new Mock<DaprClient>();
        mock.Setup(x => x.GetSecretAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(bundle);
        return mock;
    }

    private static ScriptSecretCache CreateCache(
        Mock<DaprClient> daprMock,
        FakeTimeProvider? timeProvider = null,
        SecretCacheOptions? options = null)
        => new(
            daprMock.Object,
            options ?? new SecretCacheOptions(),
            timeProvider ?? new FakeTimeProvider(),
            NullLogger<ScriptSecretCache>.Instance);

    private static void VerifyFetchCount(Mock<DaprClient> daprMock, Times times)
        => daprMock.Verify(x => x.GetSecretAsync(
            Store,
            Bundle,
            It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task SecondRead_SameBundle_DifferentKeys_HitsVaultOnce()
    {
        var daprMock = CreateDaprMock(new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        var cache = CreateCache(daprMock);

        (await cache.GetSecretAsync(Store, Bundle, "a")).ShouldBe("1");
        (await cache.GetSecretAsync(Store, Bundle, "b")).ShouldBe("2");

        VerifyFetchCount(daprMock, Times.Once());
    }

    [Fact]
    public async Task SingleSecretAndBundleReads_ShareTheSameEntry()
    {
        var daprMock = CreateDaprMock(new Dictionary<string, string> { ["a"] = "1" });
        var cache = CreateCache(daprMock);

        (await cache.GetSecretAsync(Store, Bundle, "a")).ShouldBe("1");
        (await cache.GetSecretsAsync(Store, Bundle)).ShouldContainKeyAndValue("a", "1");

        VerifyFetchCount(daprMock, Times.Once());
    }

    [Fact]
    public async Task ReadWithinTtl_ServedFromCache()
    {
        var daprMock = CreateDaprMock(new Dictionary<string, string> { ["a"] = "1" });
        var time = new FakeTimeProvider();
        var cache = CreateCache(daprMock, time);

        await cache.GetSecretAsync(Store, Bundle, "a");
        time.Advance(TimeSpan.FromSeconds(29));
        await cache.GetSecretAsync(Store, Bundle, "a");

        VerifyFetchCount(daprMock, Times.Once());
    }

    [Fact]
    public async Task ReadAfterTtlExpiry_RefetchesFromVault()
    {
        var daprMock = CreateDaprMock(new Dictionary<string, string> { ["a"] = "1" });
        var time = new FakeTimeProvider();
        var cache = CreateCache(daprMock, time);

        await cache.GetSecretAsync(Store, Bundle, "a");
        time.Advance(TimeSpan.FromSeconds(31));
        await cache.GetSecretAsync(Store, Bundle, "a");

        VerifyFetchCount(daprMock, Times.Exactly(2));
    }

    [Fact]
    public async Task ConcurrentReaders_CollapseIntoOneVaultFetch()
    {
        var gate = new TaskCompletionSource<Dictionary<string, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(x => x.GetSecretAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .Returns(gate.Task);
        var cache = CreateCache(daprMock);

        // Direct calls (no Task.Run): each async invocation runs synchronously up to the await on
        // the gated fetch, so all 20 readers are provably joined to the single in-flight Lazy
        // before the gate opens — the test is deterministic, not a scheduling race.
        var readers = Enumerable.Range(0, 20)
            .Select(_ => cache.GetSecretAsync(Store, Bundle, "a"))
            .ToArray();
        gate.SetResult(new Dictionary<string, string> { ["a"] = "1" });
        var values = await Task.WhenAll(readers);

        values.ShouldAllBe(v => v == "1");
        VerifyFetchCount(daprMock, Times.Once());
    }

    [Fact]
    public async Task FaultedFetch_IsNotCached_NextCallRetries()
    {
        var daprMock = new Mock<DaprClient>();
        daprMock.SetupSequence(x => x.GetSecretAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("vault unavailable"))
            .ReturnsAsync(new Dictionary<string, string> { ["a"] = "1" });
        var cache = CreateCache(daprMock);

        await Should.ThrowAsync<InvalidOperationException>(
            () => cache.GetSecretAsync(Store, Bundle, "a"));
        (await cache.GetSecretAsync(Store, Bundle, "a")).ShouldBe("1");

        VerifyFetchCount(daprMock, Times.Exactly(2));
    }

    [Fact]
    public async Task ConcurrentReaders_DuringFaultedFetch_AllObserveTheFailure_NothingCached()
    {
        var gate = new TaskCompletionSource<Dictionary<string, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var daprMock = new Mock<DaprClient>();
        daprMock.SetupSequence(x => x.GetSecretAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .Returns(gate.Task)
            .ReturnsAsync(new Dictionary<string, string> { ["a"] = "1" });
        var cache = CreateCache(daprMock);

        // Direct calls (no Task.Run) so every reader is awaiting the same in-flight Lazy before
        // the gate faults — otherwise a late-scheduled reader could miss the shared failure and
        // trigger its own (successful) fetch, making the assertions racy.
        var readers = Enumerable.Range(0, 10)
            .Select(_ => cache.GetSecretAsync(Store, Bundle, "a"))
            .ToArray();
        gate.SetException(new InvalidOperationException("vault unavailable"));

        foreach (var reader in readers)
            await Should.ThrowAsync<InvalidOperationException>(() => reader);

        (await cache.GetSecretAsync(Store, Bundle, "a")).ShouldBe("1");
        VerifyFetchCount(daprMock, Times.Exactly(2));
    }

    [Theory]
    [InlineData(false, 30)] // Enabled = false
    [InlineData(true, 0)]   // TtlSeconds <= 0
    public async Task Bypass_EveryReadGoesToTheVault(bool enabled, int ttlSeconds)
    {
        var daprMock = CreateDaprMock(new Dictionary<string, string> { ["a"] = "1" });
        var cache = CreateCache(daprMock, options: new SecretCacheOptions
        {
            Enabled = enabled,
            TtlSeconds = ttlSeconds
        });

        (await cache.GetSecretAsync(Store, Bundle, "a")).ShouldBe("1");
        (await cache.GetSecretAsync(Store, Bundle, "a")).ShouldBe("1");

        VerifyFetchCount(daprMock, Times.Exactly(2));
    }

    [Fact]
    public async Task MutatingReturnedBundle_DoesNotPoisonTheCache()
    {
        var daprMock = CreateDaprMock(new Dictionary<string, string> { ["a"] = "1" });
        var cache = CreateCache(daprMock);

        var first = await cache.GetSecretsAsync(Store, Bundle);
        first["a"] = "tampered";
        first["injected"] = "x";

        var second = await cache.GetSecretsAsync(Store, Bundle);
        second["a"].ShouldBe("1");
        second.ShouldNotContainKey("injected");
        (await cache.GetSecretAsync(Store, Bundle, "a")).ShouldBe("1");
    }

    [Fact]
    public void Probe_BeforeAnyFetch_Misses()
    {
        var daprMock = CreateDaprMock(new Dictionary<string, string> { ["a"] = "1" });
        var cache = CreateCache(daprMock);

        cache.TryGetCachedSecret(Store, Bundle, "a", out _).ShouldBeFalse();
        cache.TryGetCachedBundle(Store, Bundle, out _).ShouldBeFalse();
        VerifyFetchCount(daprMock, Times.Never());
    }

    [Fact]
    public async Task Probe_AfterSuccessfulFetch_HitsWithoutExtraVaultCall()
    {
        var daprMock = CreateDaprMock(new Dictionary<string, string> { ["a"] = "1" });
        var cache = CreateCache(daprMock);

        await cache.GetSecretAsync(Store, Bundle, "a");

        cache.TryGetCachedSecret(Store, Bundle, "a", out var value).ShouldBeTrue();
        value.ShouldBe("1");
        cache.TryGetCachedBundle(Store, Bundle, out var bundle).ShouldBeTrue();
        bundle!.ShouldContainKeyAndValue("a", "1");
        VerifyFetchCount(daprMock, Times.Once());
    }

    [Fact]
    public async Task Probe_MissingKeyOnCachedBundle_HitsWithEmptyString()
    {
        var daprMock = CreateDaprMock(new Dictionary<string, string> { ["a"] = "1" });
        var cache = CreateCache(daprMock);

        await cache.GetSecretAsync(Store, Bundle, "a");

        // Same contract as GetSecretAsync: bundle hit + absent key => empty string, still a hit.
        cache.TryGetCachedSecret(Store, Bundle, "missing", out var value).ShouldBeTrue();
        value.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Probe_AfterTtlExpiry_Misses_WithoutEvictingOrRefetching()
    {
        var daprMock = CreateDaprMock(new Dictionary<string, string> { ["a"] = "1" });
        var time = new FakeTimeProvider();
        var cache = CreateCache(daprMock, time);

        await cache.GetSecretAsync(Store, Bundle, "a");
        time.Advance(TimeSpan.FromSeconds(31));

        cache.TryGetCachedSecret(Store, Bundle, "a", out _).ShouldBeFalse();
        VerifyFetchCount(daprMock, Times.Once()); // probe never fetches; the async path refreshes
    }

    [Fact]
    public void Probe_WhileFetchInFlight_Misses()
    {
        var gate = new TaskCompletionSource<Dictionary<string, string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(x => x.GetSecretAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .Returns(gate.Task);
        var cache = CreateCache(daprMock);

        var pending = cache.GetSecretAsync(Store, Bundle, "a"); // installs the in-flight lazy

        cache.TryGetCachedSecret(Store, Bundle, "a", out _).ShouldBeFalse();

        gate.SetResult(new Dictionary<string, string> { ["a"] = "1" });
        pending.Result.ShouldBe("1"); // waits for the released fetch's continuations to finish
        cache.TryGetCachedSecret(Store, Bundle, "a", out var value).ShouldBeTrue();
        value.ShouldBe("1");
    }

    [Fact]
    public void Probe_WhenBypassed_Misses()
    {
        var daprMock = CreateDaprMock(new Dictionary<string, string> { ["a"] = "1" });
        var cache = CreateCache(daprMock, options: new SecretCacheOptions { Enabled = false });

        cache.TryGetCachedSecret(Store, Bundle, "a", out _).ShouldBeFalse();
        cache.TryGetCachedBundle(Store, Bundle, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Probe_ReturnedBundle_IsADefensiveCopy()
    {
        var daprMock = CreateDaprMock(new Dictionary<string, string> { ["a"] = "1" });
        var cache = CreateCache(daprMock);

        await cache.GetSecretAsync(Store, Bundle, "a");

        cache.TryGetCachedBundle(Store, Bundle, out var first).ShouldBeTrue();
        first!["a"] = "tampered";

        cache.TryGetCachedBundle(Store, Bundle, out var second).ShouldBeTrue();
        second!["a"].ShouldBe("1");
    }

    [Fact]
    public async Task MissingKey_ReturnsEmptyString()
    {
        var daprMock = CreateDaprMock(new Dictionary<string, string> { ["a"] = "1" });
        var cache = CreateCache(daprMock);

        (await cache.GetSecretAsync(Store, Bundle, "missing")).ShouldBe(string.Empty);
    }

    [Fact]
    public async Task NullVaultResponse_YieldsEmptyBundle()
    {
        var daprMock = new Mock<DaprClient>();
        daprMock.Setup(x => x.GetSecretAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Dictionary<string, string>)null!);
        var cache = CreateCache(daprMock);

        (await cache.GetSecretsAsync(Store, Bundle)).ShouldBeEmpty();
        (await cache.GetSecretAsync(Store, Bundle, "a")).ShouldBe(string.Empty);
    }
}
