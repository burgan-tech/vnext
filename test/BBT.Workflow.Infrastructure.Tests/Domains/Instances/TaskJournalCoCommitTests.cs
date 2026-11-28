using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Data;
using BBT.Workflow.DataSink;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Validation;
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
/// A task's journal row commits in the same transaction as its data-output row, against a real
/// PostgreSQL: <see cref="IInstanceDataWriteService.AppendWithAsync"/> runs the journal write inside
/// the append's row-lock transaction, and <see cref="EfCoreInstanceTaskRepository.InsertCompletedAsync"/>
/// writes the row on the shared context's connection without <c>SaveChanges</c>.
/// </summary>
[Collection(EfCoreInstanceRepositoryPrepareSpanTests.SpanCollection)]
public sealed class TaskJournalCoCommitTests : IAsyncLifetime
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
    public async Task Raw_journal_insert_stores_exactly_what_the_ef_insert_stores()
    {
        // Pins InsertCompletedAsync's hand-written column list against EF's own mapping: the same
        // task written both ways reads back identically in every column but its identity.
        var (_, transitionId) = await SeedAsync();
        var viaEf = CompletedTask(transitionId, "via-ef", order: 1);
        var viaRaw = CompletedTask(transitionId, "via-raw", order: 2);
        viaRaw.CopyTimesFrom(viaEf);

        await using (var ctx = CreateContext())
        {
            await CreateTaskRepository(ctx).InsertAsync(viaEf, autoSave: true);
        }

        await using (var ctx = CreateContext())
        {
            await CreateTaskRepository(ctx).InsertCompletedAsync(viaRaw);
            ctx.ChangeTracker.Entries().ShouldBeEmpty();
        }

        var efRow = await ReadJournalRowAsync(viaEf.Id);
        var rawRow = await ReadJournalRowAsync(viaRaw.Id);
        string[] identity = ["Id", "TaskId", "ExecutionKey", "Order"];
        efRow.Keys.ShouldBe(rawRow.Keys, ignoreOrder: true);
        foreach (var column in efRow.Keys.Except(identity))
            rawRow[column].ShouldBe(efRow[column], $"column {column}");
        rawRow["ExecutionKey"].ShouldBe(viaRaw.ExecutionKey);
        rawRow["Order"].ShouldBe("2");
    }

    [Fact]
    public async Task Data_row_and_journal_row_commit_in_one_transaction_without_flushing_the_context()
    {
        var (instance, transitionId) = await SeedAsync();
        var recorder = new Recorder();
        await using var ctx = CreateContext(recorder);
        var tracked = await ctx.Instances.SingleAsync(i => i.Id == instance.Id);
        tracked.ModifiedBy = "pending-change-of-the-hop";
        var journal = CompletedTask(transitionId, "task-with-output", order: 1);

        var row = await CreateWriteService(ctx).AppendWithAsync(
            tracked, new JsonData("{\"result\":42}"), VersionStrategy.IncreasePatch,
            ct => CreateTaskRepository(ctx).InsertCompletedAsync(journal, ct));

        row.ShouldNotBeNull();
        recorder.Commits.ShouldBe(1);
        var journalInsert = recorder.Commands.ShouldHaveSingleItem();
        journalInsert.Text.ShouldStartWith("INSERT INTO \"public\".\"InstanceTasks\"");
        journalInsert.InTransaction.ShouldBeTrue();

        // Both rows are there, and the hop's own pending change was not written by the journal.
        (await ReadJournalRowAsync(journal.Id))["Status"].ShouldBe(((int)Definitions.TaskStatus.Completed).ToString());
        (await CountDataRowsAsync(instance.Id)).ShouldBe(2);
        (await ReadModifiedByAsync(instance.Id)).ShouldNotBe("pending-change-of-the-hop");
        ctx.Entry(tracked).State.ShouldBe(EntityState.Modified);
    }

    [Fact]
    public async Task A_failing_journal_write_rolls_the_data_row_back_with_it()
    {
        var (instance, transitionId) = await SeedAsync();
        await using var ctx = CreateContext();
        var journal = CompletedTask(transitionId, "task-failing-journal", order: 1);

        await Should.ThrowAsync<InvalidOperationException>(() => CreateWriteService(ctx).AppendWithAsync(
            instance, new JsonData("{\"result\":42}"), VersionStrategy.IncreasePatch,
            async ct =>
            {
                await CreateTaskRepository(ctx).InsertCompletedAsync(journal, ct);
                throw new InvalidOperationException("journal write failed");
            }));

        (await CountDataRowsAsync(instance.Id)).ShouldBe(1);
        (await JournalRowExistsAsync(journal.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_deduplicated_append_still_commits_the_journal_row()
    {
        // Same content as the head: no data row, but the task ran and its journal row is written.
        var (instance, transitionId) = await SeedAsync();
        await using var ctx = CreateContext();
        var journal = CompletedTask(transitionId, "task-same-output", order: 1);

        var row = await CreateWriteService(ctx).AppendWithAsync(
            instance, new JsonData("{\"step\":1}"), VersionStrategy.IncreasePatch,
            ct => CreateTaskRepository(ctx).InsertCompletedAsync(journal, ct));

        row.ShouldBeNull();
        (await CountDataRowsAsync(instance.Id)).ShouldBe(1);
        (await JournalRowExistsAsync(journal.Id)).ShouldBeTrue();
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<(Instance Instance, Guid TransitionId)> SeedAsync()
    {
        var instance = Instance.Create(Guid.NewGuid(), "journal-flow", "1.0.0");
        var data = new InstanceData(Guid.NewGuid(), instance.Id, "1.0.0", new JsonData("{\"step\":1}"), true)
            { VersionNo = 1 };
        instance.AcceptPersistedData(data);
        var transition = InstanceTransition.Create(
            Guid.NewGuid(), instance.Id, "go", "start", TriggerType.Manual, new JsonData("{}"), new JsonData("{}"));

        await using var ctx = CreateContext();
        ctx.Instances.Add(instance);
        ctx.Set<InstanceTransition>().Add(transition);
        await ctx.SaveChangesAsync();
        return (instance, transition.Id);
    }

    private static InstanceTask CompletedTask(Guid transitionId, string taskId, int order)
    {
        var task = new InstanceTask(Guid.NewGuid(), transitionId, taskId, TaskTrigger.OnExecute, order);
        task.SetRequest(new JsonData("{\"url\":\"https://x.local\"}"));
        task.SetInvocationResult(new JsonData("{\"StatusCode\":200}"));
        task.Completed(new JsonData("{\"data\":{\"result\":42}}"), isBusinessSuccess: true);
        return task;
    }

    private async Task<Dictionary<string, string?>> ReadJournalRowAsync(Guid id)
    {
        await using var ctx = CreateContext();
        var connection = ctx.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM \"public\".\"InstanceTasks\" WHERE \"Id\" = @id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = id;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        var row = new Dictionary<string, string?>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
            row[reader.GetName(i)] = value switch
            {
                null => null,
                DateTime time => time.ToUniversalTime().ToString("O"),
                string text when reader.GetDataTypeName(i) == "jsonb" => JsonDocument.Parse(text).RootElement.GetRawText(),
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
            };
        }

        return row;
    }

    private async Task<bool> JournalRowExistsAsync(Guid id)
    {
        await using var ctx = CreateContext();
        return await ctx.Set<InstanceTask>().AsNoTracking().AnyAsync(t => t.Id == id);
    }

    private async Task<int> CountDataRowsAsync(Guid instanceId)
    {
        await using var ctx = CreateContext();
        return await ctx.Set<InstanceData>().AsNoTracking().CountAsync(d => d.InstanceId == instanceId);
    }

    private async Task<string?> ReadModifiedByAsync(Guid instanceId)
    {
        await using var ctx = CreateContext();
        return (await ctx.Instances.AsNoTracking().SingleAsync(i => i.Id == instanceId)).ModifiedBy;
    }

    private WorkflowDbContext CreateContext(Recorder? recorder = null)
    {
        var builder = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        if (recorder is not null)
            builder.AddInterceptors(recorder);

        return new WorkflowDbContext(builder.Options, new StaticCurrentSchema("public"));
    }

    private static EfCoreInstanceTaskRepository CreateTaskRepository(WorkflowDbContext context) => new(
        new FixedDbContextProvider(context),
        new ServiceCollection().BuildServiceProvider(),
        Substitute.For<IDataSinkManager>());

    private static InstanceDataWriteService CreateWriteService(WorkflowDbContext context)
    {
        var validator = Substitute.For<IJsonSchemaValidator>();
        validator.Validate(Arg.Any<JsonElement>(), Arg.Any<JsonElement?>()).Returns(Result.Ok());
        return new InstanceDataWriteService(
            new FixedDbContextProvider(context),
            new ServiceCollection().BuildServiceProvider(),
            validator,
            Options.Create(new WorkflowExecutionOptions()),
            NullLogger<InstanceDataWriteService>.Instance);
    }

    /// <summary>
    /// EF-issued commands (the data append's own batches go to Npgsql directly and are not seen)
    /// with whether each ran inside a transaction, and the transactions EF committed.
    /// </summary>
    private sealed class Recorder : DbCommandInterceptor, IDbTransactionInterceptor
    {
        public List<(string Text, bool InTransaction)> Commands { get; } = [];
        public int Commits { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add((command.CommandText.TrimStart(), command.Transaction is not null));
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public Task TransactionCommittedAsync(
            DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Commits++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedDbContextProvider(WorkflowDbContext context)
        : IAetherDbContextProvider<WorkflowDbContext>
    {
        public Task<WorkflowDbContext> GetDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(context);
    }
}

internal static class InstanceTaskTestExtensions
{
    /// <summary>Gives two journal entities the same timestamps so their rows can be compared.</summary>
    public static void CopyTimesFrom(this InstanceTask target, InstanceTask source)
    {
        target.CreatedAt = source.CreatedAt;
        typeof(InstanceTask).GetProperty(nameof(InstanceTask.StartedAt))!.SetValue(target, source.StartedAt);
        typeof(InstanceTask).GetProperty(nameof(InstanceTask.FinishedAt))!.SetValue(target, source.FinishedAt);
        typeof(InstanceTask).GetProperty(nameof(InstanceTask.Duration))!.SetValue(target, source.Duration);
    }
}
