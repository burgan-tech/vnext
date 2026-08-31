using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Data;
using BBT.Workflow.Instances;
using BBT.Workflow.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Integration tests for <see cref="InstanceDataWriteService"/> against a real PostgreSQL:
/// the per-instance <c>FOR UPDATE</c> row lock serializes concurrent writers, and the row's
/// whole identity (VersionNo, Version, dedup) is computed under that lock from the
/// authoritative head. These replace the old trigger-based versioning tests — the trigger was
/// dropped when identity assignment moved into the application.
/// </summary>
public sealed class InstanceDataVersioningTests : IAsyncLifetime
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

        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgresContainer.StopAsync();
        await _postgresContainer.DisposeAsync();
    }

    private WorkflowDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(_connectionString)
            .Options;

        return new WorkflowDbContext(options, new StaticCurrentSchema("public"));
    }

    private static InstanceDataWriteService CreateService(WorkflowDbContext context) => new(
        new FixedDbContextProvider(context),
        new ServiceCollection().BuildServiceProvider(),
        Substitute.For<IJsonSchemaValidator>(),
        Options.Create(new WorkflowExecutionOptions()),
        NullLogger<InstanceDataWriteService>.Instance);

    private async Task<Instance> CreateInstanceAsync(string flow = "test-flow")
    {
        await using var ctx = CreateContext();
        var instance = Instance.Create(Guid.NewGuid(), flow, "1.0.0");
        ctx.Instances.Add(instance);
        await ctx.SaveChangesAsync();
        return instance;
    }

    [Fact]
    public async Task Concurrent_Appends_For_Same_Instance_Should_Serialize_On_The_Row_Lock()
    {
        // Arrange
        var seeded = await CreateInstanceAsync();
        const int count = 20;

        // Act — every writer has its own DbContext (own connection), exactly like parallel
        // task branches in production; the FOR UPDATE lock serializes them.
        var tasks = Enumerable.Range(0, count)
            .Select(async i =>
            {
                await Task.Delay(Random.Shared.Next(0, 50));

                await using var ctx = CreateContext();
                var instance = Instance.Create(seeded.Id, seeded.Flow, seeded.FlowVersion);
                var service = CreateService(ctx);

                await service.AppendAsync(
                    instance,
                    new JsonData($"{{\"k{i}\":{i}}}"),
                    VersionStrategy.IncreasePatch,
                    CancellationToken.None);
            });

        await Task.WhenAll(tasks);

        // Assert
        await using (var ctx = CreateContext())
        {
            var list = await ctx.InstancesData
                .Where(x => x.InstanceId == seeded.Id)
                .ToListAsync();

            // a) Every append produced a row (distinct payloads — no dedup)
            list.Count.ShouldBe(count);

            // b) Each Patch append started a NEW version line — line-scoped VersionNo is 1
            //    on every row, and the version strings are all distinct (no duplicate SemVer).
            list.ShouldAllBe(x => x.VersionNo == 1);
            list.Select(x => x.Version).Distinct().Count().ShouldBe(count);

            // c) Exactly one latest
            var latest = list.Single(x => x.IsLatest);

            // d) Loss-free merge: the final row carries EVERY writer's key
            for (var i = 0; i < count; i++)
            {
                latest.Data.Json.ShouldContain($"\"k{i}\"");
            }
        }
    }

    [Fact]
    public async Task SameVersion_Appends_Should_Grow_Their_Own_Line_Ordinal()
    {
        // The review-mandated semantics: VersionNo is an ordinal WITHIN one semantic Version
        // string — None-strategy appends continue their line (1, 2, 3...) and a strategy bump
        // starts a fresh line back at 1:
        //   1.0.0|1, 1.0.0|2, 1.0.0|3, 1.1.0|1
        var seeded = await CreateInstanceAsync();

        await using var ctx = CreateContext();
        var instance = Instance.Create(seeded.Id, seeded.Flow, seeded.FlowVersion);
        var service = CreateService(ctx);

        var r1 = await service.AppendAsync(instance, new JsonData("{\"a\":1}"), null, CancellationToken.None);
        var r2 = await service.AppendAsync(instance, new JsonData("{\"a\":2}"), null, CancellationToken.None);
        var r3 = await service.AppendAsync(instance, new JsonData("{\"a\":3}"), null, CancellationToken.None);
        var r4 = await service.AppendAsync(instance, new JsonData("{\"b\":1}"), VersionStrategy.IncreaseMinor, CancellationToken.None);

        (r1!.Version, r1.VersionNo).ShouldBe(("1.0.0", 1L));
        (r2!.Version, r2.VersionNo).ShouldBe(("1.0.0", 2L));
        (r3!.Version, r3.VersionNo).ShouldBe(("1.0.0", 3L));
        (r4!.Version, r4.VersionNo).ShouldBe(("1.1.0", 1L));

        await using var verifyCtx = CreateContext();
        var list = await verifyCtx.InstancesData
            .Where(x => x.InstanceId == seeded.Id).ToListAsync();
        list.Count.ShouldBe(4);
        list.Single(x => x.IsLatest).Version.ShouldBe("1.1.0");
    }

    [Fact]
    public async Task Append_With_Identical_Merged_Content_Should_Be_A_NoOp()
    {
        // Arrange
        var seeded = await CreateInstanceAsync();

        await using var ctx = CreateContext();
        var instance = Instance.Create(seeded.Id, seeded.Flow, seeded.FlowVersion);
        var service = CreateService(ctx);

        var first = await service.AppendAsync(
            instance, new JsonData("{\"a\":1}"), VersionStrategy.IncreasePatch, CancellationToken.None);

        // Act — a delta-only duplicate: merged content is byte-identical to the head
        var duplicate = await service.AppendAsync(
            instance, new JsonData("{\"a\":1}"), VersionStrategy.IncreaseMinor, CancellationToken.None);

        // Assert
        first.ShouldNotBeNull();
        duplicate.ShouldBeNull();

        var rows = await ctx.InstancesData.Where(x => x.InstanceId == seeded.Id).ToListAsync();
        rows.Count.ShouldBe(1);
        rows.Single().Version.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task Sequential_Appends_Should_Build_Versions_From_The_Database_Head()
    {
        // Arrange
        var seeded = await CreateInstanceAsync();

        await using var ctx = CreateContext();
        var instance = Instance.Create(seeded.Id, seeded.Flow, seeded.FlowVersion);
        var service = CreateService(ctx);

        // Act
        var v1 = await service.AppendAsync(instance, new JsonData("{\"a\":1}"), null, CancellationToken.None);
        var v2 = await service.AppendAsync(instance, new JsonData("{\"b\":2}"), VersionStrategy.IncreasePatch, CancellationToken.None);
        var v3 = await service.AppendAsync(instance, new JsonData("{\"c\":3}"), VersionStrategy.IncreaseMinor, CancellationToken.None);

        // Assert — first row starts the chain, each next version derives from the DB head;
        // every strategy bump opens a new line, so each row's line-scoped VersionNo is 1.
        v1!.Version.ShouldBe("1.0.0");
        v2!.Version.ShouldBe("1.0.1");
        v3!.Version.ShouldBe("1.1.0");
        v1.VersionNo.ShouldBe(1);
        v2.VersionNo.ShouldBe(1);
        v3.VersionNo.ShouldBe(1);

        // Aggregate snapshot follows the persisted head
        instance.LatestData!.Id.ShouldBe(v3.Id);
        instance.DataList.Count(d => d.IsLatest).ShouldBe(1);
    }

    [Fact]
    public async Task Explicit_Lower_Version_Inserted_After_Higher_Should_Not_Steal_Latest()
    {
        // Regression for the IsLatest invariant: appending a lower explicit version (an older
        // artifact line, e.g. 1.0.5 while 2.0.0 is the head) must NOT steal the latest flag.
        var seeded = await CreateInstanceAsync();

        await using var ctx = CreateContext();
        var instance = Instance.Create(seeded.Id, seeded.Flow, seeded.FlowVersion);
        var service = CreateService(ctx);

        var head = await service.AppendExplicitAsync(
            instance, Guid.NewGuid(), "2.0.0", new JsonData("{\"v\":\"head\"}"), CancellationToken.None);
        var olderLine = await service.AppendExplicitAsync(
            instance, Guid.NewGuid(), "1.0.5", new JsonData("{\"v\":\"line\"}"), CancellationToken.None);

        // Same-version republish dedups to the existing row
        var republish = await service.AppendExplicitAsync(
            instance, Guid.NewGuid(), "2.0.0", new JsonData("{\"v\":\"ignored\"}"), CancellationToken.None);

        // Assert
        olderLine.IsLatest.ShouldBeFalse();
        republish.Id.ShouldBe(head.Id);

        var list = await ctx.InstancesData.Where(x => x.InstanceId == seeded.Id).ToListAsync();
        list.Count.ShouldBe(2);
        list.Single(x => x.IsLatest).Version.ShouldBe("2.0.0");
    }

    [Fact]
    public async Task Appends_After_An_Explicit_OlderLine_Row_Should_Not_Collide()
    {
        // Production regression (publish path): after 1.0.0 -> 1.1.0 -> 1.0.1 every further
        // publish or strategy append used to die on the VersionNo unique index because the
        // next number was derived from the wrong row. Line-scoped numbering makes this
        // structurally impossible — each distinct version string is its own line at 1.
        var seeded = await CreateInstanceAsync();

        await using var ctx = CreateContext();
        var instance = Instance.Create(seeded.Id, seeded.Flow, seeded.FlowVersion);
        var service = CreateService(ctx);

        await service.AppendExplicitAsync(instance, Guid.NewGuid(), "1.0.0", new JsonData("{\"v\":1}"), CancellationToken.None);
        await service.AppendExplicitAsync(instance, Guid.NewGuid(), "1.1.0", new JsonData("{\"v\":2}"), CancellationToken.None);
        var olderLine = await service.AppendExplicitAsync(instance, Guid.NewGuid(), "1.0.1", new JsonData("{\"v\":3}"), CancellationToken.None);
        olderLine.VersionNo.ShouldBe(1);
        olderLine.IsLatest.ShouldBeFalse();

        // Both directions must keep working: a higher explicit line, a lower explicit line,
        // and a strategy append bumping from the semantic head. Latest-flag asserts run at
        // the moment each row is the head — later appends legitimately demote earlier ones.
        // Every distinct version string is its own line, so each row's VersionNo is 1.
        var higher = await service.AppendExplicitAsync(instance, Guid.NewGuid(), "1.2.0", new JsonData("{\"v\":4}"), CancellationToken.None);
        higher.VersionNo.ShouldBe(1);
        higher.IsLatest.ShouldBeTrue();

        var lower = await service.AppendExplicitAsync(instance, Guid.NewGuid(), "1.0.2", new JsonData("{\"v\":5}"), CancellationToken.None);
        lower.VersionNo.ShouldBe(1);
        lower.IsLatest.ShouldBeFalse();

        var strategy = await service.AppendAsync(instance, new JsonData("{\"w\":1}"), VersionStrategy.IncreasePatch, CancellationToken.None);
        strategy!.VersionNo.ShouldBe(1);
        strategy.Version.ShouldBe("1.2.1");

        await using var verifyCtx = CreateContext();
        var list = await verifyCtx.InstancesData
            .Where(x => x.InstanceId == seeded.Id).ToListAsync();
        list.Count.ShouldBe(6);
        list.ShouldAllBe(x => x.VersionNo == 1);
        list.Single(x => x.IsLatest).Version.ShouldBe("1.2.1");
    }

    [Fact]
    public async Task Concurrent_Appends_On_A_Shared_DbContext_Should_Serialize_On_The_Context_Gate()
    {
        // Parallel task branches all receive the SAME schema-bound DbContext from the ambient
        // UnitOfWork — a Npgsql connection cannot run two commands at once, so the service
        // must serialize same-context appends instead of throwing
        // NpgsqlOperationInProgressException ("connection is already in state 'Executing'").
        var seeded = await CreateInstanceAsync();
        const int count = 8;

        await using var ctx = CreateContext();
        var service = CreateService(ctx);

        var tasks = Enumerable.Range(0, count)
            .Select(i =>
            {
                var instance = Instance.Create(seeded.Id, seeded.Flow, seeded.FlowVersion);
                return service.AppendAsync(
                    instance, new JsonData($"{{\"shared{i}\":{i}}}"), VersionStrategy.IncreasePatch,
                    CancellationToken.None);
            })
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert from a FRESH context: the writing context's tracked rows don't observe the
        // raw-SQL stale-latest demote (in production the shared aggregate's
        // AcceptPersistedData demotes the in-memory rows; here each append used a
        // throwaway aggregate on purpose).
        await using var verifyCtx = CreateContext();
        var list = await verifyCtx.InstancesData
            .Where(x => x.InstanceId == seeded.Id)
            .ToListAsync();
        list.Count.ShouldBe(count);
        // Each Patch append opened a fresh version line: line-scoped VersionNo is 1 everywhere.
        list.ShouldAllBe(x => x.VersionNo == 1);
        list.Select(x => x.Version).Distinct().Count().ShouldBe(count);
        var latest = list.Single(x => x.IsLatest);
        for (var i = 0; i < count; i++)
        {
            latest.Data.Json.ShouldContain($"\"shared{i}\"");
        }
    }

    [Fact]
    public async Task Different_Instances_Should_Have_Independent_Version_Lines()
    {
        // Arrange — same version string ("1.0.0" line) growing on two different instances:
        // the line ordinal is scoped per (InstanceId, Version), never shared across instances.
        var seeded1 = await CreateInstanceAsync("test-flow-1");
        var seeded2 = await CreateInstanceAsync("test-flow-2");

        await using var ctx = CreateContext();
        var service = CreateService(ctx);
        var instance1 = Instance.Create(seeded1.Id, seeded1.Flow, seeded1.FlowVersion);
        var instance2 = Instance.Create(seeded2.Id, seeded2.Flow, seeded2.FlowVersion);

        // Act — None strategy keeps the version string, so all rows land on each
        // instance's own "1.0.0" line
        for (var i = 0; i < 3; i++)
            await service.AppendAsync(instance1, new JsonData($"{{\"a\":{i}}}"), null, CancellationToken.None);
        for (var i = 0; i < 2; i++)
            await service.AppendAsync(instance2, new JsonData($"{{\"b\":{i}}}"), null, CancellationToken.None);

        // Assert — line ordinals are per instance
        (await ctx.InstancesData.Where(x => x.InstanceId == seeded1.Id).OrderBy(x => x.VersionNo)
            .Select(x => x.VersionNo).ToListAsync()).ShouldBe([1L, 2L, 3L]);
        (await ctx.InstancesData.Where(x => x.InstanceId == seeded2.Id).OrderBy(x => x.VersionNo)
            .Select(x => x.VersionNo).ToListAsync()).ShouldBe([1L, 2L]);
    }

    private sealed class FixedDbContextProvider(WorkflowDbContext context)
        : IAetherDbContextProvider<WorkflowDbContext>
    {
        public Task<WorkflowDbContext> GetDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(context);
    }
}
