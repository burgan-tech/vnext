using BBT.Workflow.HttpApi.Shared.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace BBT.Workflow.HealthChecks;

public sealed class CachedHealthCheckTests
{
    private static HealthCheckContext MakeContext() => new()
    {
        Registration = new HealthCheckRegistration(
            "test",
            new DummyHealthCheck(),
            null,
            null)
    };

    private sealed class DummyHealthCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(HealthCheckResult.Healthy());
    }

    // FakeTimeProvider: 1 tick = 1 saniye (TimestampFrequency = 1)
    private sealed class FakeTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)by.TotalSeconds);
    }

    private sealed class StubHealthCheck(Func<HealthCheckResult> factory) : IHealthCheck
    {
        public int CallCount { get; private set; }

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(factory());
        }
    }

    [Fact]
    public async Task FirstCall_HitsInnerCheck()
    {
        var inner = new StubHealthCheck(() => HealthCheckResult.Healthy());
        var sut = new CachedHealthCheck(inner, TimeSpan.FromSeconds(60), new FakeTimeProvider());

        await sut.CheckHealthAsync(MakeContext());

        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task SecondCallWithinTtl_ReturnsCachedResult_NoExtraDbHit()
    {
        var inner = new StubHealthCheck(() => HealthCheckResult.Healthy());
        var fake = new FakeTimeProvider();
        var sut = new CachedHealthCheck(inner, TimeSpan.FromSeconds(60), fake);

        await sut.CheckHealthAsync(MakeContext());
        fake.Advance(TimeSpan.FromSeconds(59));
        await sut.CheckHealthAsync(MakeContext());

        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task CallAfterTtlExpires_HitsInnerCheckAgain()
    {
        var inner = new StubHealthCheck(() => HealthCheckResult.Healthy());
        var fake = new FakeTimeProvider();
        var sut = new CachedHealthCheck(inner, TimeSpan.FromSeconds(60), fake);

        await sut.CheckHealthAsync(MakeContext()); // t=0, hit inner
        fake.Advance(TimeSpan.FromSeconds(60));     // t=60, TTL expired
        await sut.CheckHealthAsync(MakeContext()); // hit inner again

        inner.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task CachesUnhealthyResult_UntilTtlExpires()
    {
        var inner = new StubHealthCheck(() => HealthCheckResult.Unhealthy("db down"));
        var fake = new FakeTimeProvider();
        var sut = new CachedHealthCheck(inner, TimeSpan.FromSeconds(60), fake);

        var first = await sut.CheckHealthAsync(MakeContext());
        fake.Advance(TimeSpan.FromSeconds(30));
        var second = await sut.CheckHealthAsync(MakeContext());

        first.Status.ShouldBe(HealthStatus.Unhealthy);
        second.Status.ShouldBe(HealthStatus.Unhealthy);
        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentCalls_HitInnerOnlyOnce()
    {
        var inner = new StubHealthCheck(() =>
        {
            Thread.Sleep(10); // simulate slow DB
            return HealthCheckResult.Healthy();
        });
        var sut = new CachedHealthCheck(inner, TimeSpan.FromSeconds(60), new FakeTimeProvider());
        var context = MakeContext();

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => sut.CheckHealthAsync(context))
            .Cast<Task>()
            .ToArray();
        await Task.WhenAll(tasks);

        inner.CallCount.ShouldBe(1);
    }
}
