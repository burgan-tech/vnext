using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.Data;
using BBT.Workflow.Definitions;
using BBT.Workflow.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using BBT.Aether.MultiSchema;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Metrics;

/// <summary>
/// Pins <see cref="EfCoreFunctionExecutionRepository"/> against a real PostgreSQL (the raw
/// <c>percentile_cont</c> summary and the fixed <c>sys_metrics</c> schema cannot be represented by the
/// shared SQLite entry point): the filtered paged read (newest first, HasNext without a count) and the
/// aggregate (count / p50 / p95 / failure-rate) — vnext-client-sdk-core#60, items C1/D.
/// </summary>
public sealed class FunctionExecutionPersistenceTests : IAsyncLifetime
{
    private const string Domain = "metrics-domain";
    private const string FunctionKey = "get-report";

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("testdb").WithUsername("test").WithPassword("test")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.StopAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task QueryAsync_ReturnsMatchingRowsNewestFirst_AndFiltersByScopeWindowAndOutcome()
    {
        var t0 = DateTime.UtcNow.AddMinutes(-30);
        await SeedAsync(
            Row(FunctionKey, "D", null, null, t0.AddMinutes(1), 100, succeeded: true),
            Row(FunctionKey, "F", "north-star", null, t0.AddMinutes(2), 200, succeeded: false),
            Row(FunctionKey, "I", "north-star", Guid.NewGuid(), t0.AddMinutes(3), 300, succeeded: true),
            Row("other-fn", "D", null, null, t0.AddMinutes(4), 400, succeeded: true));

        await using var ctx = CreateContext();
        var repo = new EfCoreFunctionExecutionRepository(new FixedDbContextProvider(ctx), Sp(), Substitute.For<ICurrentSchema>());

        // key filter + newest first
        var all = await repo.QueryAsync(new FunctionExecutionQuery(FunctionKey), CancellationToken.None);
        all.Items.Count.ShouldBe(3);
        all.Items.ShouldNotContain(e => e.FunctionKey == "other-fn");
        all.Items.Select(e => e.Scope).ShouldBe(new[] { "I", "F", "D" }); // newest first (t0+3,+2,+1)

        // workflow narrows to flow/instance rows
        var flow = await repo.QueryAsync(new FunctionExecutionQuery(FunctionKey, Workflow: "north-star"), CancellationToken.None);
        flow.Items.Count.ShouldBe(2);
        flow.Items.ShouldAllBe(e => e.Workflow == "north-star");

        // outcome filter
        var failed = await repo.QueryAsync(new FunctionExecutionQuery(FunctionKey, Succeeded: false), CancellationToken.None);
        failed.Items.ShouldHaveSingleItem().Scope.ShouldBe("F");

        // window filter
        var windowed = await repo.QueryAsync(
            new FunctionExecutionQuery(FunctionKey, From: t0.AddMinutes(2).AddSeconds(-1), To: t0.AddMinutes(2).AddSeconds(1)),
            CancellationToken.None);
        windowed.Items.ShouldHaveSingleItem().Scope.ShouldBe("F");
    }

    [Fact]
    public async Task QueryAsync_PagesWithHasNext()
    {
        var t0 = DateTime.UtcNow.AddMinutes(-10);
        var rows = Enumerable.Range(0, 5)
            .Select(i => Row(FunctionKey, "D", null, null, t0.AddSeconds(i), 10 * (i + 1), succeeded: true))
            .ToArray();
        await SeedAsync(rows);

        await using var ctx = CreateContext();
        var repo = new EfCoreFunctionExecutionRepository(new FixedDbContextProvider(ctx), Sp(), Substitute.For<ICurrentSchema>());

        var page1 = await repo.QueryAsync(new FunctionExecutionQuery(FunctionKey, Page: 1, PageSize: 2), CancellationToken.None);
        page1.Items.Count.ShouldBe(2);
        page1.HasNext.ShouldBeTrue();

        var page3 = await repo.QueryAsync(new FunctionExecutionQuery(FunctionKey, Page: 3, PageSize: 2), CancellationToken.None);
        page3.Items.Count.ShouldBe(1);
        page3.HasNext.ShouldBeFalse();
    }

    [Fact]
    public async Task SummarizeAsync_ComputesCountPercentilesAndFailureRate()
    {
        var t0 = DateTime.UtcNow.AddMinutes(-10);
        // durations 10..100 (step 10); 2 of the 10 failed → failureRate 0.2
        var rows = Enumerable.Range(0, 10)
            .Select(i => Row(FunctionKey, "D", null, null, t0.AddSeconds(i), 10 * (i + 1), succeeded: i >= 2))
            .ToArray();
        await SeedAsync(rows);

        await using var ctx = CreateContext();
        var repo = new EfCoreFunctionExecutionRepository(new FixedDbContextProvider(ctx), Sp(), Substitute.For<ICurrentSchema>());

        var summary = await repo.SummarizeAsync(new FunctionExecutionQuery(FunctionKey), CancellationToken.None);

        summary.Count.ShouldBe(10);
        summary.FailureRate.ShouldBe(0.2, 0.0001);
        // percentile_cont interpolates: p50 of 10..100 is 55, p95 is 95.5.
        summary.P50Ms!.Value.ShouldBe(55, 0.001);
        summary.P95Ms!.Value.ShouldBe(95.5, 0.001);
    }

    [Fact]
    public async Task SummarizeAsync_EmptyWindow_ReturnsZeroAndNullPercentiles()
    {
        await using var ctx = CreateContext();
        var repo = new EfCoreFunctionExecutionRepository(new FixedDbContextProvider(ctx), Sp(), Substitute.For<ICurrentSchema>());

        var summary = await repo.SummarizeAsync(new FunctionExecutionQuery("never-run"), CancellationToken.None);

        summary.Count.ShouldBe(0);
        summary.P50Ms.ShouldBeNull();
        summary.P95Ms.ShouldBeNull();
        summary.FailureRate.ShouldBe(0);
    }

    private static FunctionExecution Row(
        string key, string scope, string? workflow, Guid? instanceId,
        DateTime invokedAt, double durationMs, bool succeeded) =>
        FunctionExecution.Record(
            Guid.NewGuid(), Domain, key, "1.0.0", TaskScope.FromCode(scope), workflow, instanceId,
            invokedAt, durationMs, succeeded, succeeded ? 200 : null, succeeded ? null : "Task:Http:500", fromCache: false);

    private async Task SeedAsync(params FunctionExecution[] rows)
    {
        await using var ctx = CreateContext();
        ctx.FunctionExecutions.AddRange(rows);
        await ctx.SaveChangesAsync();
    }

    private MetricsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MetricsDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new MetricsDbContext(options);
    }

    private static IServiceProvider Sp() => new ServiceCollection().BuildServiceProvider();

    private sealed class FixedDbContextProvider(MetricsDbContext context)
        : IAetherDbContextProvider<MetricsDbContext>
    {
        public Task<MetricsDbContext> GetDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(context);
    }
}
