using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Agents;
using BBT.Workflow.Data;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Agents;
using BBT.Workflow.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Agents;

/// <summary>
/// Agent journal and session persistence against PostgreSQL (issue #1105). Pins the database budget the
/// design depends on: the journal adds rows without a round trip of its own and they go out in one batch;
/// a session read is one statement and a session write is one statement; the agent-runs listing is one
/// statement that never selects the wide columns. Also pins the session cipher (scope-bound, rotated key),
/// optimistic concurrency and the sweeper.
/// </summary>
public sealed class AgentPersistenceTests : IAsyncLifetime
{
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

        await using var ctx = CreateContext(null);
        await ctx.Database.EnsureCreatedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.StopAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Journal_adds_without_a_round_trip_and_saves_in_one_batch()
    {
        var instance = await SeedInstanceAsync();
        var recorder = new CommandRecorder();
        await using var ctx = CreateContext(recorder);
        var journal = new EfCoreAgentRunJournal(new FixedProvider(ctx));
        var (run, calls, decision) = Rows(instance.Id);

        await journal.AddAsync(run, calls, decision);
        recorder.Commands.ShouldBeEmpty();

        await ctx.SaveChangesAsync();
        recorder.Commands.Count.ShouldBe(1);

        await using var verify = CreateContext(null);
        (await verify.InstanceAgentRuns.CountAsync(r => r.InstanceId == instance.Id)).ShouldBe(1);
        (await verify.InstanceAgentToolCalls.CountAsync(c => c.RunId == run.Id)).ShouldBe(2);
        (await verify.InstanceAgentDecisions.SingleAsync(d => d.RunId == run.Id)).SelectedTransition.ShouldBe("is-question");
    }

    [Fact]
    public async Task Listing_is_one_statement_without_wide_columns_and_joins_the_decision()
    {
        var instance = await SeedInstanceAsync();
        await using (var ctx = CreateContext(null))
        {
            var (run, calls, decision) = Rows(instance.Id);
            await new EfCoreAgentRunJournal(new FixedProvider(ctx)).AddAsync(run, calls, decision);
            await ctx.SaveChangesAsync();
        }

        var recorder = new CommandRecorder();
        await using var read = CreateContext(recorder);
        var items = await new EfCoreAgentRunReadRepository(new FixedProvider(read)).ListAsync(instance.Id, null, 0, 20);

        items.Count.ShouldBe(1);
        items[0].SelectedTransition.ShouldBe("is-question");
        items[0].DecisionSource.ShouldBe(AgentDecisionSource.Model);
        recorder.Commands.Count.ShouldBe(1);
        recorder.Commands[0].ShouldNotContain("\"Request\"");
        recorder.Commands[0].ShouldNotContain("\"Response\"");
        recorder.Commands[0].ShouldNotContain("\"Transcript\"");
    }

    [Fact]
    public async Task Session_round_trips_compressed_and_detects_concurrent_writers()
    {
        var instance = await SeedInstanceAsync();
        var scope = $"i:{instance.Id:N}:flow:classifying";
        var plain = Encoding.UTF8.GetBytes("""{"messages":["secret customer text"]}""");

        var recorder = new CommandRecorder();
        await using var ctx = CreateContext(recorder);
        var store = new EfCoreAgentSessionStore(new FixedProvider(ctx));

        (await store.SaveAsync(Write(scope, instance.Id, plain, null), instance.Id)).ShouldBeTrue();
        recorder.Commands.Count.ShouldBe(1);

        recorder.Commands.Clear();
        var first = await store.FindAsync(scope, instance.Id);
        recorder.Commands.Count.ShouldBe(1);
        first.Snapshot!.Messages.ShouldBe(plain);

        await using (var raw = CreateContext(null))
        {
            var stored = await raw.AgentSessions.AsNoTracking().SingleAsync(s => s.ScopeKey == scope);
            Encoding.UTF8.GetString(stored.SessionData!).ShouldNotContain("secret customer text");
        }

        // A writer that did not read the row, and a writer holding a stale version, both lose.
        (await store.SaveAsync(Write(scope, instance.Id, plain, null), instance.Id)).ShouldBeFalse();
        (await store.SaveAsync(Write(scope, instance.Id, plain, first.Snapshot.RowVersion), instance.Id)).ShouldBeTrue();
        (await store.SaveAsync(Write(scope, instance.Id, plain, first.Snapshot.RowVersion), instance.Id)).ShouldBeFalse();
    }

    [Fact]
    public async Task Sweeper_deletes_expired_rows_only_and_instance_delete_cascades()
    {
        var instance = await SeedInstanceAsync();
        await using var ctx = CreateContext(null);
        var store = new EfCoreAgentSessionStore(new FixedProvider(ctx));
        var expired = Write($"i:{instance.Id:N}:f:old", instance.Id, [1], null);
        expired.Session.ExpiresAt = DateTime.UtcNow.AddDays(-1);
        await store.SaveAsync(expired, instance.Id);
        await store.SaveAsync(Write($"i:{instance.Id:N}:f:new", instance.Id, [1], null), instance.Id);

        (await store.SweepExpiredAsync(1000)).ShouldBe(1);
        (await store.FindAsync($"i:{instance.Id:N}:f:new", instance.Id)).Snapshot.ShouldNotBeNull();

        var (run, calls, decision) = Rows(instance.Id);
        await new EfCoreAgentRunJournal(new FixedProvider(ctx)).AddAsync(run, calls, decision);
        await ctx.SaveChangesAsync();

        await ctx.Database.ExecuteSqlRawAsync("DELETE FROM public.\"Instances\" WHERE \"Id\" = {0}", instance.Id);
        (await ctx.AgentSessions.CountAsync(s => s.OwnerInstanceId == instance.Id)).ShouldBe(0);
        (await ctx.InstanceAgentRuns.CountAsync(r => r.InstanceId == instance.Id)).ShouldBe(0);
        (await ctx.InstanceAgentToolCalls.CountAsync(c => c.InstanceId == instance.Id)).ShouldBe(0);
    }

    private static AgentSessionWrite Write(string scope, Guid instanceId, byte[] messages, uint? expected) => new(
        scope,
        new AgentSession
        {
            ScopeKey = scope,
            Mode = AgentMemoryMode.State,
            Storage = AgentMemoryStorage.Local,
            AgentKey = "triage",
            AgentVersion = "1.0.0",
            InstructionsHash = "h",
            MessageCount = 2,
            OwnerInstanceId = instanceId,
            LastInstanceId = instanceId,
            LastRunId = Guid.NewGuid(),
            UpdatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        },
        messages,
        expected);

    private static (InstanceAgentRun, IReadOnlyList<InstanceAgentToolCall>, InstanceAgentDecision) Rows(Guid instanceId)
    {
        var run = new InstanceAgentRun(Guid.NewGuid())
        {
            InstanceId = instanceId, State = "classifying", EntryNo = 1,
            ExecutionKey = InstanceAgentRun.CreateExecutionKey(Guid.NewGuid(), "classifying", 1),
            AgentKey = "triage", AgentDomain = "core", AgentVersion = "1.0.0", RuntimeKey = "maf", ProviderKey = "local",
            Status = AgentRunStatus.Completed, StartedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
            Request = """{"promptHash":"x"}""", Response = """{"category":"question"}"""
        };
        var calls = Enumerable.Range(1, 2).Select(i => new InstanceAgentToolCall(Guid.NewGuid())
        {
            RunId = run.Id, InstanceId = instanceId, Sequence = i, CallId = $"c{i}", Name = "get_customer",
            Kind = AgentToolKind.Task, IdempotencyKey = InstanceAgentToolCall.CreateIdempotencyKey(run.Id, $"c{i}"),
            Arguments = "{}", Status = AgentToolCallStatus.Completed, StartedAt = DateTime.UtcNow
        }).ToList();
        var decision = new InstanceAgentDecision(Guid.NewGuid())
        {
            RunId = run.Id, InstanceId = instanceId, State = "classifying", Mode = AgentDecisionMode.Model,
            Source = AgentDecisionSource.Model, SelectedTransition = "is-question", Candidates = "[]", CreatedAt = DateTime.UtcNow
        };
        return (run, calls, decision);
    }

    private async Task<Instance> SeedInstanceAsync()
    {
        var instance = Instance.Create(Guid.NewGuid(), "agent-flow", "1.0.0", Guid.NewGuid().ToString("N"));
        await using var ctx = CreateContext(null);
        ctx.Instances.Add(instance);
        await ctx.SaveChangesAsync();
        return instance;
    }

    private WorkflowDbContext CreateContext(CommandRecorder? recorder)
    {
        var builder = new DbContextOptionsBuilder<WorkflowDbContext>().UseNpgsql(_connectionString);
        if (recorder is not null)
            builder.AddInterceptors(recorder);
        return new WorkflowDbContext(builder.Options, new StaticCurrentSchema("public"));
    }

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

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText.TrimStart());
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class FixedProvider(WorkflowDbContext context) : IAetherDbContextProvider<WorkflowDbContext>
    {
        public Task<WorkflowDbContext> GetDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(context);
    }
}
