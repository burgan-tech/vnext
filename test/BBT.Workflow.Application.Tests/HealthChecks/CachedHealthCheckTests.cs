using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.HttpApi.Shared.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.HealthChecks;

public class CachedHealthCheckTests
{
    private static HealthCheckContext Ctx() => new()
    {
        Registration = new HealthCheckRegistration("db", Substitute.For<IHealthCheck>(), null, null)
    };

    [Fact]
    public async Task Within_ttl_inner_is_called_once()
    {
        var inner = Substitute.For<IHealthCheck>();
        inner.CheckHealthAsync(Arg.Any<HealthCheckContext>(), Arg.Any<CancellationToken>())
             .Returns(HealthCheckResult.Healthy());

        var clock = new FakeTimeProvider();
        var sut = new CachedHealthCheck(inner, TimeSpan.FromSeconds(10), clock);

        await sut.CheckHealthAsync(Ctx());
        clock.Advance(TimeSpan.FromSeconds(5));
        var second = await sut.CheckHealthAsync(Ctx());

        second.Status.ShouldBe(HealthStatus.Healthy);
        await inner.Received(1).CheckHealthAsync(Arg.Any<HealthCheckContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task After_ttl_inner_is_reevaluated()
    {
        var inner = Substitute.For<IHealthCheck>();
        inner.CheckHealthAsync(Arg.Any<HealthCheckContext>(), Arg.Any<CancellationToken>())
             .Returns(HealthCheckResult.Healthy());

        var clock = new FakeTimeProvider();
        var sut = new CachedHealthCheck(inner, TimeSpan.FromSeconds(10), clock);

        await sut.CheckHealthAsync(Ctx());
        clock.Advance(TimeSpan.FromSeconds(11));
        await sut.CheckHealthAsync(Ctx());

        await inner.Received(2).CheckHealthAsync(Arg.Any<HealthCheckContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stale_result_from_unhealthy_inner_is_replaced_after_ttl()
    {
        var inner = Substitute.For<IHealthCheck>();
        inner.CheckHealthAsync(Arg.Any<HealthCheckContext>(), Arg.Any<CancellationToken>())
             .Returns(HealthCheckResult.Unhealthy("down"), HealthCheckResult.Healthy());

        var clock = new FakeTimeProvider();
        var sut = new CachedHealthCheck(inner, TimeSpan.FromSeconds(10), clock);

        var first = await sut.CheckHealthAsync(Ctx());
        first.Status.ShouldBe(HealthStatus.Unhealthy);

        clock.Advance(TimeSpan.FromSeconds(11));

        var second = await sut.CheckHealthAsync(Ctx());
        second.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Concurrent_calls_invoke_inner_only_once()
    {
        var callCount = 0;
        var inner = Substitute.For<IHealthCheck>();
        inner.CheckHealthAsync(Arg.Any<HealthCheckContext>(), Arg.Any<CancellationToken>())
             .Returns(async _ =>
             {
                 Interlocked.Increment(ref callCount);
                 await Task.Delay(20);
                 return HealthCheckResult.Healthy();
             });

        var clock = new FakeTimeProvider();
        var sut = new CachedHealthCheck(inner, TimeSpan.FromSeconds(10), clock);

        var ctx = Ctx();
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => sut.CheckHealthAsync(ctx))
            .ToArray();

        await Task.WhenAll(tasks);

        callCount.ShouldBe(1);
    }

    [Fact]
    public async Task Pre_cancelled_token_throws_OperationCanceledException()
    {
        var inner = Substitute.For<IHealthCheck>();
        var clock = new FakeTimeProvider();
        var sut = new CachedHealthCheck(inner, TimeSpan.FromSeconds(10), clock);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => sut.CheckHealthAsync(Ctx(), cts.Token));
    }

    /// <summary>
    /// Minimal controllable <see cref="TimeProvider"/> for tests — advances time on demand.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
