using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Data;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Security;
using BBT.Workflow.Runtime;
using BBT.Workflow.DataSink;
using BBT.Workflow.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// <c>Instances.ModifiedAt</c> against a real PostgreSQL: it moves when a transition decides its outcome
/// (any state change, a same-state one included, a completion, a cancel, a fault) and when updateData
/// writes a new data row. A transition's stamp rides on the UPDATE its save sends for the outcome; an
/// append made inside a transition writes no instance row version at all; updateData adds one UPDATE
/// per data row it writes, inside the batch that inserts that row.
/// <para>
/// Row versions are observed through <c>xmin</c>: every UPDATE creates a new version with a new
/// <c>xmin</c>, while the writer's <c>FOR UPDATE</c> lock does not change it.
/// </para>
/// </summary>
public sealed class InstanceModifiedAtStampTests(InstanceModifiedAtStampTests.PostgresFixture fixture)
    : IClassFixture<InstanceModifiedAtStampTests.PostgresFixture>
{
    /// <summary>Postgres stores microseconds; a .NET timestamp carries 100 ns ticks.</summary>
    private static readonly TimeSpan StoragePrecision = TimeSpan.FromMilliseconds(1);

    /// <summary>Long enough that a stamp taken after it is distinguishable from one taken before.</summary>
    private const int ClockStepMs = 20;

    /// <summary><c>WorkflowLogs.InstanceDataStaleLatestDemoted</c>.</summary>
    private const int StaleLatestDemotedEventId = 10143;

    public sealed class PostgresFixture : IAsyncLifetime
    {
        private PostgreSqlContainer _container = null!;

        public string ConnectionString { get; private set; } = null!;

        async Task IAsyncLifetime.InitializeAsync()
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("testdb")
                .WithUsername("test")
                .WithPassword("test")
                .Build();

            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();

            await using var context = CreateContext(ConnectionString);
            await context.Database.EnsureCreatedAsync();
        }

        async Task IAsyncLifetime.DisposeAsync()
        {
            await _container.StopAsync();
            await _container.DisposeAsync();
        }
    }

    private sealed class CapturingLogger : ILogger<InstanceDataWriteService>
    {
        public List<int> EventIds { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => EventIds.Add(eventId.Id);
    }

    private static WorkflowDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new WorkflowDbContext(options, new StaticCurrentSchema("public"));
    }

    private WorkflowDbContext CreateContext() => CreateContext(fixture.ConnectionString);

    private static InstanceDataWriteService CreateService(
        WorkflowDbContext context, ILogger<InstanceDataWriteService>? logger = null) => new(
        new FixedDbContextProvider(context),
        new ServiceCollection().BuildServiceProvider(),
        Substitute.For<IJsonSchemaValidator>(),
        Options.Create(new WorkflowExecutionOptions()),
        logger ?? NullLogger<InstanceDataWriteService>.Instance);

    private static EfCoreInstanceRepository CreateRepository(WorkflowDbContext context) => new(
        new FixedDbContextProvider(context),
        new ServiceCollection().BuildServiceProvider(),
        Substitute.For<IRuntimeInfoProvider>(),
        Substitute.For<IDataSinkManager>(),
        new StaticCurrentSchema("public"),
        Substitute.For<ISchemaValidator>(),
        Options.Create(new WorkflowExecutionOptions()),
        NullLogger<EfCoreInstanceRepository>.Instance);

    private static State StateNamed(string key) =>
        State.Create(key, StateType.Intermediate, StateSubType.None, VersionStrategy.IncreasePatch.Code);

    private async Task<Instance> CreateInstanceAsync(string state = "draft")
    {
        await using var ctx = CreateContext();
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0");
        instance.ChangeState(StateNamed(state));
        ctx.Instances.Add(instance);
        await ctx.SaveChangesAsync();
        return instance;
    }

    private async Task<(DateTime ModifiedAt, long Xmin)> ReadRowAsync(Guid instanceId)
    {
        await using var ctx = CreateContext();
        var modifiedAt = await ctx.Instances.AsNoTracking()
            .Where(i => i.Id == instanceId)
            .Select(i => i.ModifiedAt)
            .SingleAsync();
        var xmin = await ctx.Database
            .SqlQueryRaw<long>("SELECT xmin::text::bigint AS \"Value\" FROM \"Instances\" WHERE \"Id\" = {0}", instanceId)
            .SingleAsync();

        modifiedAt.ShouldNotBeNull();
        return (modifiedAt.Value, xmin);
    }

    private async Task<int> CountDataRowsAsync(Guid instanceId)
    {
        await using var ctx = CreateContext();
        return await ctx.InstancesData.CountAsync(d => d.InstanceId == instanceId);
    }

    public static TheoryData<string> TerminalOutcomes => ["Complete", "Cancel", "Fault"];

    private static void End(Instance instance, string outcome)
    {
        switch (outcome)
        {
            case "Complete": instance.Complete("test-domain"); break;
            case "Cancel": instance.Cancel("test-domain"); break;
            default: instance.Fault("test-domain"); break;
        }
    }

    /// <summary>
    /// The reported case: a finished instance must not show a time from before its final transition.
    /// The terminal outcome stamps ModifiedAt with the same instant as CompletedAt.
    /// </summary>
    [Theory]
    [MemberData(nameof(TerminalOutcomes))]
    public async Task Terminal_outcome_stamps_ModifiedAt_with_CompletedAt(string outcome)
    {
        var seeded = await CreateInstanceAsync();
        var before = await ReadRowAsync(seeded.Id);
        await Task.Delay(ClockStepMs);

        DateTime completedAt;
        await using (var ctx = CreateContext())
        {
            var instance = await ctx.Instances.SingleAsync(i => i.Id == seeded.Id);
            End(instance, outcome);
            completedAt = instance.CompletedAt!.Value;
            await ctx.SaveChangesAsync();
        }

        var after = await ReadRowAsync(seeded.Id);
        after.ModifiedAt.ShouldBeGreaterThan(before.ModifiedAt);
        after.ModifiedAt.ShouldBe(completedAt, StoragePrecision);
    }

    /// <summary>A transition that parks the instance (no Busy release follows) is stamped by its state change.</summary>
    [Fact]
    public async Task State_change_stamps_ModifiedAt()
    {
        var seeded = await CreateInstanceAsync("draft");
        var before = await ReadRowAsync(seeded.Id);
        await Task.Delay(ClockStepMs);

        await using (var ctx = CreateContext())
        {
            var instance = await ctx.Instances.SingleAsync(i => i.Id == seeded.Id);
            instance.ChangeState(StateNamed("review"));
            await ctx.SaveChangesAsync();
        }

        (await ReadRowAsync(seeded.Id)).ModifiedAt.ShouldBeGreaterThan(before.ModifiedAt);
    }

    /// <summary>
    /// A same-state hop that ends without a Busy release (parked in Busy, waiting on a SubFlow, or not
    /// holding the status) has no other write that carries its stamp: <c>ChangeStateStep</c> leaves it
    /// pending and the settlement stamps the aggregate (<c>MarkModified</c>), which the commit sends — the
    /// one case that costs an UPDATE of its own.
    /// </summary>
    [Fact]
    public async Task Same_state_hop_closed_without_a_release_writes_one_row_version()
    {
        var seeded = await CreateInstanceAsync("draft");
        var before = await ReadRowAsync(seeded.Id);
        await Task.Delay(ClockStepMs);

        await using (var ctx = CreateContext())
        {
            var instance = await ctx.Instances.SingleAsync(i => i.Id == seeded.Id);
            instance.ChangeState(StateNamed("draft")).ShouldBeFalse();   // ChangeStateStep: nothing moved, pending
            instance.MarkModified();                                     // TransitionSettlement
            await ctx.SaveChangesAsync();
        }

        var after = await ReadRowAsync(seeded.Id);
        after.ModifiedAt.ShouldBeGreaterThan(before.ModifiedAt);
        after.Xmin.ShouldNotBe(before.Xmin);
    }

    /// <summary>
    /// <c>ChangeStateStep</c>'s save of an unmoved state — updateData's <c>$self</c> (its data row stamps
    /// it), or a same-state hop whose stamp is left pending, such as the start transition into the
    /// pre-positioned initial state (its Busy release stamps it) — stays a no-op: no UPDATE, no new row
    /// version.
    /// </summary>
    [Fact]
    public async Task Unstamped_same_state_save_writes_nothing()
    {
        var seeded = await CreateInstanceAsync("draft");
        var before = await ReadRowAsync(seeded.Id);

        await using (var ctx = CreateContext())
        {
            var instance = await ctx.Instances.SingleAsync(i => i.Id == seeded.Id);
            instance.ChangeState(StateNamed("draft"));
            await ctx.SaveChangesAsync();
        }

        (await ReadRowAsync(seeded.Id)).ShouldBe(before);
    }

    /// <summary>
    /// An updateData whose request changes only the stage writes no data row; the stage change stamps
    /// ModifiedAt itself, riding the UPDATE its <c>$self</c> save sends for the new stage.
    /// </summary>
    [Fact]
    public async Task UpdateData_that_changes_only_the_stage_stamps_ModifiedAt()
    {
        var seeded = await CreateInstanceAsync("draft");
        var before = await ReadRowAsync(seeded.Id);
        await Task.Delay(ClockStepMs);

        await using (var ctx = CreateContext())
        {
            var instance = await ctx.Instances.SingleAsync(i => i.Id == seeded.Id);
            instance.SetStage("under-review");                                // CreateTransitionRecordStep
            instance.ChangeState(StateNamed("draft"));                        // ChangeStateStep, updateData
            await ctx.SaveChangesAsync();
        }

        (await ReadRowAsync(seeded.Id)).ModifiedAt.ShouldBeGreaterThan(before.ModifiedAt);
    }

    /// <summary>
    /// A same-state hop that rests Active is closed by the Busy release, so settlement leaves its pending stamp
    /// to that CAS (<c>TransitionSettlement</c>: <c>!flipped</c>). The release must therefore stamp ModifiedAt
    /// itself, or such a hop — the start into the pre-positioned initial state, a <c>$self</c> shared
    /// transition — would go unstamped.
    /// </summary>
    [Fact]
    public async Task The_Busy_release_that_closes_a_hop_stamps_ModifiedAt()
    {
        var seeded = await CreateInstanceAsync("draft");
        await using (var seed = CreateContext())
        {
            await seed.Instances.Where(i => i.Id == seeded.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.Status, InstanceStatus.Busy));
        }
        var before = await ReadRowAsync(seeded.Id);
        await Task.Delay(ClockStepMs);

        await using (var ctx = CreateContext())
        {
            var instance = await ctx.Instances.SingleAsync(i => i.Id == seeded.Id);
            (await CreateRepository(ctx).TryReleaseBusyAsync(instance)).ShouldBeTrue();
        }

        (await ReadRowAsync(seeded.Id)).ModifiedAt.ShouldBeGreaterThan(before.ModifiedAt);
    }

    /// <summary>
    /// The long-poll arm closes a same-state hop that pauses there (<c>HandleLongPollTerminationStep</c> closes
    /// the pending stamp), so the arm must stamp ModifiedAt itself.
    /// </summary>
    [Fact]
    public async Task The_long_poll_arm_stamps_ModifiedAt()
    {
        var seeded = await CreateInstanceAsync("draft");
        var before = await ReadRowAsync(seeded.Id);
        await Task.Delay(ClockStepMs);

        await using (var ctx = CreateContext())
        {
            await CreateRepository(ctx).ArmLongPollAckAsync(seeded.Id, Guid.NewGuid());
        }

        (await ReadRowAsync(seeded.Id)).ModifiedAt.ShouldBeGreaterThan(before.ModifiedAt);
    }

    /// <summary>A detached aggregate saved through Update() carries the stamp the domain method set.</summary>
    [Fact]
    public async Task Detached_terminal_update_stamps_ModifiedAt()
    {
        var seeded = await CreateInstanceAsync();

        Instance detached;
        await using (var ctx = CreateContext())
            detached = await ctx.Instances.AsNoTracking().SingleAsync(i => i.Id == seeded.Id);

        await Task.Delay(ClockStepMs);
        detached.Fault("test-domain");

        await using (var ctx = CreateContext())
        {
            ctx.Instances.Update(detached);
            await ctx.SaveChangesAsync();
        }

        (await ReadRowAsync(seeded.Id)).ModifiedAt.ShouldBe(detached.CompletedAt!.Value, StoragePrecision);
    }

    /// <summary>
    /// The hot path: request bodies and task outputs are appended inside a transition, whose outcome
    /// stamps the instance once. The append itself must write no instance row version.
    /// </summary>
    [Fact]
    public async Task Append_inside_a_transition_writes_no_instance_row()
    {
        var seeded = await CreateInstanceAsync();
        var before = await ReadRowAsync(seeded.Id);

        await using (var ctx = CreateContext())
        {
            var instance = Instance.Create(seeded.Id, seeded.Flow, seeded.FlowVersion);
            var row = await CreateService(ctx).AppendAsync(
                instance, new JsonData("{\"amount\":1}"), null, CancellationToken.None);
            row.ShouldNotBeNull();
        }

        (await ReadRowAsync(seeded.Id)).ShouldBe(before);
    }

    /// <summary>updateData: no state change, so the data row is the modification and stamps its EnteredAt.</summary>
    [Fact]
    public async Task UpdateData_append_stamps_ModifiedAt_with_the_row_EnteredAt()
    {
        var seeded = await CreateInstanceAsync();
        await Task.Delay(ClockStepMs);

        await using var ctx = CreateContext();
        var instance = Instance.Create(seeded.Id, seeded.Flow, seeded.FlowVersion);
        var row = await CreateService(ctx).AppendAsync(
            instance, new JsonData("{\"amount\":1}"), null, CancellationToken.None, touchInstance: true);

        row.ShouldNotBeNull();
        (await ReadRowAsync(seeded.Id)).ModifiedAt.ShouldBe(row.EnteredAt, StoragePrecision);
        instance.ModifiedAt.ShouldBe(row.EnteredAt);
    }

    /// <summary>An updateData whose payload changes nothing writes no row and so changes nothing.</summary>
    [Fact]
    public async Task UpdateData_with_an_identical_payload_writes_nothing()
    {
        var seeded = await CreateInstanceAsync();
        await Task.Delay(ClockStepMs);

        await using var ctx = CreateContext();
        var service = CreateService(ctx);
        var instance = Instance.Create(seeded.Id, seeded.Flow, seeded.FlowVersion);

        var first = await service.AppendAsync(
            instance, new JsonData("{\"amount\":1}"), null, CancellationToken.None, touchInstance: true);
        var afterFirst = await ReadRowAsync(seeded.Id);
        await Task.Delay(ClockStepMs);
        var second = await service.AppendAsync(
            instance, new JsonData("{\"amount\":1}"), null, CancellationToken.None, touchInstance: true);

        first.ShouldNotBeNull();
        afterFirst.ModifiedAt.ShouldBe(first.EnteredAt, StoragePrecision);
        second.ShouldBeNull();
        (await ReadRowAsync(seeded.Id)).ShouldBe(afterFirst);
    }

    /// <summary>
    /// The pipeline's tracked aggregate receives the stamp already persisted. updateData's later
    /// ChangeState($self) save must stay a no-op — no second UPDATE for the same value.
    /// </summary>
    [Fact]
    public async Task UpdateData_stamp_leaves_the_tracked_aggregate_in_sync()
    {
        var seeded = await CreateInstanceAsync("draft");
        await Task.Delay(ClockStepMs);

        await using var ctx = CreateContext();
        var instance = await ctx.Instances.SingleAsync(i => i.Id == seeded.Id);
        var row = await CreateService(ctx).AppendAsync(
            instance, new JsonData("{\"amount\":1}"), null, CancellationToken.None, touchInstance: true);
        var afterAppend = await ReadRowAsync(seeded.Id);

        instance.ChangeState(StateNamed("draft"));
        await ctx.SaveChangesAsync();

        row.ShouldNotBeNull();
        afterAppend.ModifiedAt.ShouldBe(row.EnteredAt, StoragePrecision);
        ctx.Entry(instance).Property(i => i.ModifiedAt).IsModified.ShouldBeFalse();
        (await ReadRowAsync(seeded.Id)).ShouldBe(afterAppend);
    }

    /// <summary>
    /// The acknowledgement rewrites only the ModifiedAt snapshot, so a change made before the append and
    /// not yet detected — updateData's stage, set before its data row — must still reach the database with
    /// the next save, beside the stamp the batch already wrote.
    /// </summary>
    [Fact]
    public async Task UpdateData_stamp_keeps_an_undetected_change_for_the_next_save()
    {
        var seeded = await CreateInstanceAsync("draft");
        await Task.Delay(ClockStepMs);

        await using var ctx = CreateContext();
        var instance = await ctx.Instances.SingleAsync(i => i.Id == seeded.Id);
        instance.SetStage("under-review");
        var row = await CreateService(ctx).AppendAsync(
            instance, new JsonData("{\"amount\":1}"), null, CancellationToken.None, touchInstance: true);

        instance.ChangeState(StateNamed("draft"));
        await ctx.SaveChangesAsync();

        row.ShouldNotBeNull();
        (await ReadRowAsync(seeded.Id)).ModifiedAt.ShouldBe(row.EnteredAt, StoragePrecision);
        await using var verify = CreateContext();
        (await verify.Instances.AsNoTracking().SingleAsync(i => i.Id == seeded.Id)).Stage.ShouldBe("under-review");
    }

    /// <summary>
    /// Production runs the writer inside the unit of work's ambient transaction, not one of its own:
    /// the stamp commits with the data row.
    /// </summary>
    [Fact]
    public async Task UpdateData_stamp_commits_with_the_ambient_transaction()
    {
        var seeded = await CreateInstanceAsync();
        await Task.Delay(ClockStepMs);

        await using var ctx = CreateContext();
        InstanceData? row;
        await using (var transaction = await ctx.Database.BeginTransactionAsync())
        {
            var instance = Instance.Create(seeded.Id, seeded.Flow, seeded.FlowVersion);
            row = await CreateService(ctx).AppendAsync(
                instance, new JsonData("{\"amount\":1}"), null, CancellationToken.None, touchInstance: true);
            await transaction.CommitAsync();
        }

        row.ShouldNotBeNull();
        (await ReadRowAsync(seeded.Id)).ModifiedAt.ShouldBe(row.EnteredAt, StoragePrecision);
    }

    /// <summary>
    /// Every updateData row stamps, the last one wins: a task output written after the request body in
    /// the same transaction moves ModifiedAt again. A "stamp once" shortcut keyed on the transaction was
    /// tried and reverted — it left the instance on its first row.
    /// </summary>
    [Fact]
    public async Task UpdateData_rows_of_one_transaction_end_on_the_last_row()
    {
        var seeded = await CreateInstanceAsync();
        await Task.Delay(ClockStepMs);

        await using var ctx = CreateContext();
        InstanceData? last;
        await using (var transaction = await ctx.Database.BeginTransactionAsync())
        {
            var instance = Instance.Create(seeded.Id, seeded.Flow, seeded.FlowVersion);
            var service = CreateService(ctx);
            await service.AppendAsync(
                instance, new JsonData("{\"amount\":1}"), null, CancellationToken.None, touchInstance: true);
            last = await service.AppendAsync(
                instance, new JsonData("{\"amount\":2}"), null, CancellationToken.None, touchInstance: true);
            await transaction.CommitAsync();
        }

        last.ShouldNotBeNull();
        (await ReadRowAsync(seeded.Id)).ModifiedAt.ShouldBe(last.EnteredAt, StoragePrecision);
    }

    /// <summary>
    /// Consecutive hops on one context — one pooled connection, whose transaction object Npgsql reuses —
    /// each move ModifiedAt. This is the case the reverted transaction-keyed shortcut broke: the second
    /// hop's rows wrote no stamp at all.
    /// </summary>
    [Fact]
    public async Task UpdateData_hops_in_consecutive_transactions_each_stamp()
    {
        var seeded = await CreateInstanceAsync();
        await using var ctx = CreateContext();
        var service = CreateService(ctx);

        foreach (var amount in new[] { 1, 2 })
        {
            await Task.Delay(ClockStepMs);
            InstanceData? row;
            await using (var transaction = await ctx.Database.BeginTransactionAsync())
            {
                var instance = Instance.Create(seeded.Id, seeded.Flow, seeded.FlowVersion);
                row = await service.AppendAsync(
                    instance, new JsonData($"{{\"amount\":{amount}}}"), null, CancellationToken.None, touchInstance: true);
                await transaction.CommitAsync();
            }

            row.ShouldNotBeNull();
            (await ReadRowAsync(seeded.Id)).ModifiedAt.ShouldBe(row.EnteredAt, StoragePrecision);
        }
    }

    /// <summary>...and rolls back with it: no data row, no stamp.</summary>
    [Fact]
    public async Task UpdateData_stamp_rolls_back_with_the_ambient_transaction()
    {
        var seeded = await CreateInstanceAsync();
        var before = await ReadRowAsync(seeded.Id);
        await Task.Delay(ClockStepMs);

        await using var ctx = CreateContext();
        await using (var transaction = await ctx.Database.BeginTransactionAsync())
        {
            var instance = Instance.Create(seeded.Id, seeded.Flow, seeded.FlowVersion);
            var row = await CreateService(ctx).AppendAsync(
                instance, new JsonData("{\"amount\":1}"), null, CancellationToken.None, touchInstance: true);
            row.ShouldNotBeNull();

            // Inside the transaction the stamp is there — so what the rollback removes is real.
            var stamped = await ctx.Database
                .SqlQueryRaw<DateTime>("SELECT \"ModifiedAt\" AS \"Value\" FROM \"Instances\" WHERE \"Id\" = {0}", seeded.Id)
                .SingleAsync();
            stamped.ShouldBe(row.EnteredAt, StoragePrecision);

            await transaction.RollbackAsync();
        }

        (await ReadRowAsync(seeded.Id)).ShouldBe(before);
        (await CountDataRowsAsync(seeded.Id)).ShouldBe(0);
    }

    /// <summary>
    /// The demote log reads the demote command's own row count now that the batch carries a variable
    /// number of statements: it fires when an existing latest row was demoted, and only then.
    /// </summary>
    [Fact]
    public async Task Demote_is_logged_only_when_a_latest_row_was_demoted()
    {
        var seeded = await CreateInstanceAsync();
        var logger = new CapturingLogger();

        await using var ctx = CreateContext();
        var service = CreateService(ctx, logger);
        var instance = Instance.Create(seeded.Id, seeded.Flow, seeded.FlowVersion);

        await service.AppendAsync(instance, new JsonData("{\"amount\":1}"), null, CancellationToken.None, touchInstance: true);
        logger.EventIds.ShouldNotContain(StaleLatestDemotedEventId);

        await service.AppendAsync(instance, new JsonData("{\"amount\":2}"), null, CancellationToken.None, touchInstance: true);
        logger.EventIds.Count(id => id == StaleLatestDemotedEventId).ShouldBe(1);
    }

    private sealed class FixedDbContextProvider(WorkflowDbContext context)
        : IAetherDbContextProvider<WorkflowDbContext>
    {
        public Task<WorkflowDbContext> GetDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(context);
    }
}
