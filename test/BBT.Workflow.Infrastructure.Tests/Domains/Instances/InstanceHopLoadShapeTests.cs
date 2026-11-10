using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Aether.Users;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Data;
using BBT.Workflow.DataSink;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Pins the database commands of the pipeline's hop-1 instance load and of the Busy mirror that
/// follows the sync reserve, against a real PostgreSQL (same harness as
/// <see cref="EfCoreInstanceRepositoryPrepareSpanTests"/>):
/// <list type="bullet">
/// <item>With latest-only loading, <see cref="EfCoreInstanceRepository.FindByIdentifierAsync"/>
/// loads the same aggregate (latest data row + open correlations) in two commands instead of the
/// split query's three; so does the no-tracking <see cref="EfCoreInstanceRepository.FindByIdentifierAsReadOnlyAsync"/>
/// behind the read endpoints, and the state function's combined load reads every correlation once.</item>
/// <item><see cref="EfCoreInstanceRepository.AcceptPersistedBusyAsync"/> mirrors a Busy the CAS
/// already committed without the next SaveChanges writing <c>Status</c> again.</item>
/// </list>
/// </summary>
[Collection(EfCoreInstanceRepositoryPrepareSpanTests.SpanCollection)]
public sealed class InstanceHopLoadShapeTests : IAsyncLifetime
{
    private PostgreSqlContainer _postgresContainer = null!;
    private string _connectionString = null!;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgresContainer = new PostgreSqlBuilder("postgres:16-alpine")
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

    [Fact]
    public async Task LatestOnly_hop_load_uses_two_commands_and_keeps_the_aggregate_shape()
    {
        var seeded = await SeedAsync("hop-load-latest");
        var recorder = new CommandRecorder();
        await using var ctx = CreateContext(recorder);
        var repository = CreateRepository(ctx, latestOnly: true);

        var loaded = await repository.FindByIdentifierAsync(seeded.Id.ToString(), CancellationToken.None);

        loaded.ShouldNotBeNull();
        recorder.Commands.Count.ShouldBe(2);
        loaded!.DataList.Count.ShouldBe(1);
        loaded.DataList.Single().IsLatest.ShouldBeTrue();
        loaded.ChildCorrelations.Count.ShouldBe(2);
        loaded.ChildCorrelations.ShouldAllBe(c => !c.IsCompleted);
        loaded.HasActiveSubFlow.ShouldBeTrue();
        ctx.Entry(loaded).State.ShouldBe(EntityState.Unchanged);
    }

    [Fact]
    public async Task LatestOnly_hop_load_resolves_a_key_identifier()
    {
        var seeded = await SeedAsync("hop-load-by-key");
        await using var ctx = CreateContext();
        var repository = CreateRepository(ctx, latestOnly: true);

        var loaded = await repository.FindByIdentifierAsync("hop-load-by-key", CancellationToken.None);

        loaded.ShouldNotBeNull();
        loaded!.Id.ShouldBe(seeded.Id);
        loaded.DataList.Count.ShouldBe(1);
        loaded.ChildCorrelations.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Full_history_load_keeps_the_split_query()
    {
        // Latest-only off: DataList is the whole history, so the split (three commands) stays —
        // joining it with the correlations would multiply the history rows.
        var seeded = await SeedAsync("hop-load-full");
        var recorder = new CommandRecorder();
        await using var ctx = CreateContext(recorder);
        var repository = CreateRepository(ctx, latestOnly: false);

        var loaded = await repository.FindByIdentifierAsync(seeded.Id.ToString(), CancellationToken.None);

        loaded.ShouldNotBeNull();
        recorder.Commands.Count.ShouldBe(3);
        loaded!.DataList.Count.ShouldBe(2);
        loaded.ChildCorrelations.Count.ShouldBe(2);
    }

    [Fact]
    public async Task LatestOnly_read_only_load_uses_two_commands_and_keeps_the_aggregate_shape()
    {
        // The read endpoints' load (GET instance, data/view/schema/master functions): was the split
        // query's three commands, now the instance + latest row in one and the open correlations in
        // a second — the same aggregate, still detached.
        var seeded = await SeedAsync("read-only-latest");
        var recorder = new CommandRecorder();
        await using var ctx = CreateContext(recorder);
        var repository = CreateRepository(ctx, latestOnly: true);

        var loaded = await repository.FindByIdentifierAsReadOnlyAsync(seeded.Id.ToString(), CancellationToken.None);

        loaded.ShouldNotBeNull();
        recorder.Commands.Count.ShouldBe(2);
        recorder.Commands[1].ShouldContain("\"InstancesCorrelations\"");
        loaded!.DataList.Count.ShouldBe(1);
        loaded.DataList.Single().IsLatest.ShouldBeTrue();
        loaded.IsDataPartiallyLoaded.ShouldBeTrue();
        loaded.ChildCorrelations.Count.ShouldBe(2);
        loaded.ChildCorrelations.ShouldAllBe(c => !c.IsCompleted);
        loaded.HasActiveSubFlow.ShouldBeTrue();
        ctx.Entry(loaded).State.ShouldBe(EntityState.Detached);
        ctx.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task LatestOnly_read_only_load_resolves_a_key_identifier()
    {
        var seeded = await SeedAsync("read-only-by-key");
        await using var ctx = CreateContext();
        var repository = CreateRepository(ctx, latestOnly: true);

        var loaded = await repository.FindByIdentifierAsReadOnlyAsync("read-only-by-key", CancellationToken.None);

        loaded.ShouldNotBeNull();
        loaded!.Id.ShouldBe(seeded.Id);
        loaded.DataList.Count.ShouldBe(1);
        loaded.ChildCorrelations.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Read_only_load_answers_null_for_an_unknown_identifier()
    {
        await SeedAsync("read-only-known");
        await using var ctx = CreateContext();
        var repository = CreateRepository(ctx, latestOnly: true);

        (await repository.FindByIdentifierAsReadOnlyAsync(Guid.NewGuid().ToString(), CancellationToken.None))
            .ShouldBeNull();
        var (instance, all) = await repository.FindByIdentifierAsReadOnlyWithAllCorrelationsAsync(
            "read-only-unknown", CancellationToken.None);
        instance.ShouldBeNull();
        all.ShouldBeEmpty();
    }

    [Fact]
    public async Task Full_history_read_only_load_keeps_the_split_query()
    {
        var seeded = await SeedAsync("read-only-full");
        var recorder = new CommandRecorder();
        await using var ctx = CreateContext(recorder);
        var repository = CreateRepository(ctx, latestOnly: false);

        var loaded = await repository.FindByIdentifierAsReadOnlyAsync(seeded.Id.ToString(), CancellationToken.None);

        loaded.ShouldNotBeNull();
        recorder.Commands.Count.ShouldBe(3);
        loaded!.DataList.Count.ShouldBe(2);
        loaded.IsDataPartiallyLoaded.ShouldBeFalse();
        loaded.ChildCorrelations.Count.ShouldBe(2);
    }

    [Fact]
    public async Task State_load_reads_every_correlation_once_and_derives_the_open_set()
    {
        // The state function's load: the aggregate (open-only collection) AND the full set it lists,
        // from ONE correlation command — previously the open-correlation include plus a separate
        // GetByParentAsync read.
        var seeded = await SeedAsync("state-load-latest");
        var recorder = new CommandRecorder();
        await using var ctx = CreateContext(recorder);
        var repository = CreateRepository(ctx, latestOnly: true);

        var (loaded, all) = await repository.FindByIdentifierAsReadOnlyWithAllCorrelationsAsync(
            seeded.Id.ToString(), CancellationToken.None);

        loaded.ShouldNotBeNull();
        recorder.Commands.Count.ShouldBe(2);
        recorder.Commands.Count(c => c.Contains("\"InstancesCorrelations\"")).ShouldBe(1);
        all.Count.ShouldBe(3);
        all.Count(c => c.IsCompleted).ShouldBe(1);
        loaded!.ChildCorrelations.Count.ShouldBe(2);
        loaded.ChildCorrelations.ShouldAllBe(c => !c.IsCompleted);
        loaded.HasActiveSubFlow.ShouldBeTrue();
        loaded.DataList.Count.ShouldBe(1);
        loaded.IsDataPartiallyLoaded.ShouldBeTrue();
        ctx.ChangeTracker.Entries().ShouldBeEmpty();

        // Same rows and the same ORDER BY as the dedicated correlation read it replaces (the rows
        // tie on ParentState/CompletedAt here, so the order itself is compared through the SQL).
        var correlationSql = recorder.Commands.Single(c => c.Contains("\"InstancesCorrelations\""));
        recorder.Commands.Clear();
        var expected = await new EfCoreInstanceCorrelationRepository(
                new FixedDbContextProvider(ctx), new ServiceCollection().BuildServiceProvider())
            .GetByParentAsync(seeded.Id, CancellationToken.None);
        all.Select(c => c.Id).ShouldBe(expected.Select(c => c.Id), ignoreOrder: true);
        OrderByClause(correlationSql).ShouldBe(OrderByClause(recorder.Commands.Single()));
    }

    [Fact]
    public async Task Full_history_state_load_reads_every_correlation_once()
    {
        var seeded = await SeedAsync("state-load-full");
        var recorder = new CommandRecorder();
        await using var ctx = CreateContext(recorder);
        var repository = CreateRepository(ctx, latestOnly: false);

        var (loaded, all) = await repository.FindByIdentifierAsReadOnlyWithAllCorrelationsAsync(
            "state-load-full", CancellationToken.None);

        loaded.ShouldNotBeNull();
        recorder.Commands.Count.ShouldBe(3);
        all.Count.ShouldBe(3);
        loaded!.DataList.Count.ShouldBe(2);
        loaded.ChildCorrelations.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AcceptPersistedBusy_does_not_write_Status_again_on_the_next_save()
    {
        var seeded = await SeedAsync("busy-mirror", withCorrelations: false);
        var recorder = new CommandRecorder();
        await using var ctx = CreateContext(recorder);
        var repository = CreateRepository(ctx, latestOnly: true);
        var loaded = (await repository.FindByIdentifierAsync(seeded.Id.ToString(), CancellationToken.None))!;

        await MarkBusyInAnotherUnitOfWorkAsync(seeded.Id);
        await repository.AcceptPersistedBusyAsync(loaded, CancellationToken.None);

        loaded.IsBusy.ShouldBeTrue();
        loaded.SetStage("after-reserve");
        recorder.Commands.Clear();
        await ctx.SaveChangesAsync();

        var update = recorder.Commands.Single(c => c.StartsWith("UPDATE", StringComparison.Ordinal));
        update.ShouldContain("\"Stage\"");
        update.ShouldNotContain("\"Status\"");
        (await ReadStatusAsync(seeded.Id)).ShouldBe(InstanceStatus.Busy.Code);
    }

    [Fact]
    public async Task AcceptPersistedBusy_keeps_the_write_while_the_audit_stamp_is_pending()
    {
        // The removed write was also what stamped an empty ModifiedBy with the caller. While that
        // stamp is pending (first change of the instance, caller known), the write must stay — the
        // response's metadata.modifiedBy depends on it.
        var seeded = await SeedAsync("busy-audit-pending", withCorrelations: false);
        var recorder = new CommandRecorder();
        await using var ctx = CreateContext(recorder);
        var repository = CreateRepository(ctx, latestOnly: true, CallerServices("alice"));
        var loaded = (await repository.FindByIdentifierAsync(seeded.Id.ToString(), CancellationToken.None))!;
        loaded.ModifiedBy.ShouldBeNullOrEmpty();

        await MarkBusyInAnotherUnitOfWorkAsync(seeded.Id);
        await repository.AcceptPersistedBusyAsync(loaded, CancellationToken.None);

        loaded.IsBusy.ShouldBeTrue();
        ctx.Entry(loaded).Property(nameof(Instance.Status)).IsModified.ShouldBeTrue();
    }

    [Fact]
    public async Task AcceptPersistedBusy_drops_the_write_once_the_audit_stamp_is_done()
    {
        var seeded = await SeedAsync("busy-audit-done", withCorrelations: false, modifiedBy: "alice");
        var recorder = new CommandRecorder();
        await using var ctx = CreateContext(recorder);
        var repository = CreateRepository(ctx, latestOnly: true, CallerServices("alice"));
        var loaded = (await repository.FindByIdentifierAsync(seeded.Id.ToString(), CancellationToken.None))!;

        await MarkBusyInAnotherUnitOfWorkAsync(seeded.Id);
        await repository.AcceptPersistedBusyAsync(loaded, CancellationToken.None);

        ctx.Entry(loaded).Property(nameof(Instance.Status)).IsModified.ShouldBeFalse();
    }

    [Fact]
    public async Task In_memory_Busy_alone_writes_Status_again_on_the_next_save()
    {
        // Control for the test above: this is the redundant write AcceptPersistedBusyAsync removes.
        var seeded = await SeedAsync("busy-control", withCorrelations: false);
        var recorder = new CommandRecorder();
        await using var ctx = CreateContext(recorder);
        var repository = CreateRepository(ctx, latestOnly: true);
        var loaded = (await repository.FindByIdentifierAsync(seeded.Id.ToString(), CancellationToken.None))!;

        await MarkBusyInAnotherUnitOfWorkAsync(seeded.Id);
        loaded.Busy();
        loaded.SetStage("after-reserve");
        recorder.Commands.Clear();
        await ctx.SaveChangesAsync();

        recorder.Commands.Single(c => c.StartsWith("UPDATE", StringComparison.Ordinal))
            .ShouldContain("\"Status\"");
    }

    private static string OrderByClause(string sql) => sql[sql.IndexOf("ORDER BY", StringComparison.Ordinal)..];

    private async Task<Instance> SeedAsync(string key, bool withCorrelations = true, string? modifiedBy = null)
    {
        var instance = Instance.Create(Guid.NewGuid(), "hop-load-flow", "1.0.0", key);
        if (modifiedBy is not null)
        {
            instance.ModifiedBy = modifiedBy;
            instance.ModifiedByBehalfOf = modifiedBy;
        }
        var history = new InstanceData(Guid.NewGuid(), instance.Id, "1.0.0", new JsonData("{\"step\":1}"), false)
            { VersionNo = 1 };
        var latest = new InstanceData(Guid.NewGuid(), instance.Id, "1.0.1", new JsonData("{\"step\":2}"), true)
            { VersionNo = 1 };
        instance.AcceptPersistedData(history);
        instance.AcceptPersistedData(latest);

        if (withCorrelations)
        {
            var completed = CreateCorrelation(instance.Id, "S");
            completed.Completed();
            instance.AddCorrelation(completed);
            instance.AddCorrelation(CreateCorrelation(instance.Id, "S"));
            instance.AddCorrelation(CreateCorrelation(instance.Id, "P"));
        }

        await using var ctx = CreateContext();
        ctx.Instances.Add(instance);
        await ctx.SaveChangesAsync();
        return instance;
    }

    private async Task MarkBusyInAnotherUnitOfWorkAsync(Guid instanceId)
    {
        // What sync admission's reserve does: a set-based CAS on its own context.
        await using var other = CreateContext();
        var flipped = await CreateRepository(other, latestOnly: true)
            .TryMarkBusyAsync(instanceId, CancellationToken.None);
        flipped.ShouldBeTrue();
    }

    private async Task<string> ReadStatusAsync(Guid instanceId)
    {
        await using var ctx = CreateContext();
        var row = await ctx.Instances.AsNoTracking().SingleAsync(i => i.Id == instanceId);
        return row.Status.Code;
    }

    private static InstanceCorrelation CreateCorrelation(Guid instanceId, string subFlowType) =>
        InstanceCorrelation.Create(
            id: Guid.NewGuid(),
            instanceId: instanceId,
            parentState: "review",
            subFlowInstanceId: Guid.NewGuid(),
            subFlowType: subFlowType,
            subFlowDomain: "sub-domain",
            subFlowName: "sub-flow",
            subFlowVersion: "1.0.0");

    private WorkflowDbContext CreateContext(CommandRecorder? recorder = null)
    {
        var builder = new DbContextOptionsBuilder<WorkflowDbContext>().UseNpgsql(_connectionString);
        if (recorder is not null)
        {
            builder.AddInterceptors(recorder);
        }

        return new WorkflowDbContext(builder.Options, new StaticCurrentSchema("public"));
    }

    private static ServiceProvider CallerServices(string user)
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.ActorUserName.Returns(user);
        currentUser.UserName.Returns(user);
        return new ServiceCollection().AddSingleton(currentUser).BuildServiceProvider();
    }

    private static EfCoreInstanceRepository CreateRepository(
        WorkflowDbContext context, bool latestOnly, IServiceProvider? services = null) => new(
        new FixedDbContextProvider(context),
        services ?? new ServiceCollection().BuildServiceProvider(),
        Substitute.For<IRuntimeInfoProvider>(),
        Substitute.For<IDataSinkManager>(),
        new StaticCurrentSchema("public"),
        Substitute.For<ISchemaValidator>(),
        Options.Create(new WorkflowExecutionOptions { LatestOnlyInstanceLoading = latestOnly }),
        NullLogger<EfCoreInstanceRepository>.Instance);

    /// <summary>Records the text of every command EF sends, in order.</summary>
    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText.TrimStart());
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText.TrimStart());
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class FixedDbContextProvider(WorkflowDbContext context)
        : IAetherDbContextProvider<WorkflowDbContext>
    {
        public Task<WorkflowDbContext> GetDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(context);
    }
}
