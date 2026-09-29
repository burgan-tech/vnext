using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Data;
using BBT.Workflow.DataSink;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;
using TaskStatus = BBT.Workflow.Definitions.TaskStatus;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Pins the reads behind the tasks and actions system functions against a real
/// PostgreSQL (the shared SQLite entry point cannot represent the jsonb model):
/// <list type="bullet">
///   <item><see cref="EfCoreInstanceTaskRepository.GetHistoryByInstanceIdAsync"/> — execution
///   order, transition context joined, and the column projection contract: payload columns stay in
///   the database, the Response column travels only for Faulted rows;</item>
///   <item><see cref="EfCoreInstanceTaskRepository.GetRefForInstanceAsync"/> — a task resolves only
///   through its owning instance;</item>
///   <item><see cref="EfCoreInstanceActionRepository.GetByTaskIdAsync"/> — execution order,
///   task-scoped. Rows are inserted directly since nothing in the runtime writes InstanceActions
///   yet; this pins the read contract for when a writer lands.</item>
/// </list>
/// </summary>
public sealed class InstanceTaskHistoryPersistenceTests : IAsyncLifetime
{
    private const string Flow = "task-history-flow";

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
    public async Task GetHistoryByInstanceIdAsync_ProjectsMetadataInExecutionOrder()
    {
        var transition = await SeedInstanceWithTransitionAsync("history-order");
        var tasks = new InstanceTask[3];
        await using (var ctx = CreateContext())
        {
            for (var i = 0; i < 3; i++)
            {
                tasks[i] = new InstanceTask(Guid.NewGuid(), transition.Id, $"task-{i}", TaskTrigger.OnExecute, i);
                ctx.InstanceTasks.Add(tasks[i]);
                await Task.Delay(5);
            }
            tasks[1].Completed(JsonData.CreateFrom("""{"big":"payload"}"""), isBusinessSuccess: true);
            tasks[2].Faulted("connection refused");
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = CreateContext();
        var repository = CreateTaskRepository(readCtx);

        var rows = await repository.GetHistoryByInstanceIdAsync(transition.InstanceId);

        rows.Count.ShouldBe(3);
        rows.Select(r => r.Id).ShouldBe([tasks[0].Id, tasks[1].Id, tasks[2].Id]);
        rows[0].TransitionKey.ShouldBe("test-transition");
        rows[0].FromState.ShouldBe("InitialState");
        rows[0].TriggerType.ShouldBe(TriggerType.Manual);

        // The Response column travels only for Faulted rows — a completed row's payload stays behind.
        rows[0].FaultedResponseJson.ShouldBeNull();
        rows[1].Status.ShouldBe(TaskStatus.Completed);
        rows[1].FaultedResponseJson.ShouldBeNull();
        rows[2].Status.ShouldBe(TaskStatus.Faulted);
        rows[2].FaultedResponseJson.ShouldNotBeNull();
        rows[2].FaultedResponseJson.ShouldContain("connection refused");
    }

    /// <summary>
    /// vnext-client-sdk-core#60: the same task key can run under two hooks of one transition
    /// (a state's OnEntry vs the transition's OnExecute). Before the hook/order columns the two
    /// journal rows were indistinguishable in the projection; now Hook + Order tell them apart.
    /// </summary>
    [Fact]
    public async Task GetHistoryByInstanceIdAsync_DistinguishesTasksByHook()
    {
        var transition = await SeedInstanceWithTransitionAsync("hook-projection");
        await using (var ctx = CreateContext())
        {
            // Same task key, two different hooks — the collision the client could not resolve.
            ctx.InstanceTasks.Add(new InstanceTask(Guid.NewGuid(), transition.Id, "shared-task", TaskTrigger.OnEntry, 0));
            ctx.InstanceTasks.Add(new InstanceTask(Guid.NewGuid(), transition.Id, "shared-task", TaskTrigger.OnExecute, 2));
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = CreateContext();
        var rows = await CreateTaskRepository(readCtx).GetHistoryByInstanceIdAsync(transition.InstanceId);

        var byHook = rows.Where(r => r.TaskKey == "shared-task").ToList();
        byHook.Count.ShouldBe(2);
        byHook.ShouldContain(r => r.Hook == TaskTrigger.OnEntry && r.Order == 0);
        byHook.ShouldContain(r => r.Hook == TaskTrigger.OnExecute && r.Order == 2);
    }

    /// <summary>
    /// A row written before the columns existed carries null hook/order — the API reports unknown
    /// rather than fabricating (the value cannot be recovered from the one-way ExecutionKey hash).
    /// Simulated by clearing the columns directly, the shape a pre-migration row has on disk.
    /// </summary>
    [Fact]
    public async Task GetHistoryByInstanceIdAsync_LegacyRowsReportNullHookAndOrder()
    {
        var transition = await SeedInstanceWithTransitionAsync("hook-legacy");
        var task = new InstanceTask(Guid.NewGuid(), transition.Id, "legacy-task", TaskTrigger.OnExecute, 1);
        await using (var ctx = CreateContext())
        {
            ctx.InstanceTasks.Add(task);
            await ctx.SaveChangesAsync();
            // Emulate a pre-migration row: the columns are null on disk.
            await ctx.Database.ExecuteSqlRawAsync(
                "UPDATE public.\"InstanceTasks\" SET \"TaskTrigger\" = NULL, \"Order\" = NULL WHERE \"Id\" = {0}",
                task.Id);
        }

        await using var readCtx = CreateContext();
        var rows = await CreateTaskRepository(readCtx).GetHistoryByInstanceIdAsync(transition.InstanceId);

        var row = rows.Single(r => r.TaskKey == "legacy-task");
        row.Hook.ShouldBeNull();
        row.Order.ShouldBeNull();
    }

    [Fact]
    public async Task GetRefForInstanceAsync_ResolvesOnlyThroughOwningInstance()
    {
        var transition = await SeedInstanceWithTransitionAsync("ref-scoping");
        var task = new InstanceTask(Guid.NewGuid(), transition.Id, "test-task", TaskTrigger.OnExecute, 1);
        await using (var ctx = CreateContext())
        {
            ctx.InstanceTasks.Add(task);
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = CreateContext();
        var repository = CreateTaskRepository(readCtx);

        var taskRef = await repository.GetRefForInstanceAsync(transition.InstanceId, task.Id);
        taskRef.ShouldNotBeNull();
        taskRef.Id.ShouldBe(task.Id);
        taskRef.TaskKey.ShouldBe("test-task");

        (await repository.GetRefForInstanceAsync(Guid.NewGuid(), task.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task GetByTaskIdAsync_ReturnsExecutionOrderScopedToTask()
    {
        var transition = await SeedInstanceWithTransitionAsync("action-scoping");
        var task = new InstanceTask(Guid.NewGuid(), transition.Id, "test-task", TaskTrigger.OnExecute, 1);
        var otherTask = new InstanceTask(Guid.NewGuid(), transition.Id, "other-task", TaskTrigger.OnExecute, 2);
        var actions = new InstanceAction[3];
        await using (var ctx = CreateContext())
        {
            ctx.InstanceTasks.AddRange(task, otherTask);
            for (var i = 0; i < 3; i++)
            {
                actions[i] = new InstanceAction(Guid.NewGuid(), task.Id, $"step-{i}", JsonData.CreateFrom("""{"n":1}"""));
                ctx.InstanceActions.Add(actions[i]);
                await Task.Delay(5);
            }
            ctx.InstanceActions.Add(new InstanceAction(Guid.NewGuid(), otherTask.Id, "other", JsonData.CreateFrom("{}")));
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = CreateContext();
        var repository = new EfCoreInstanceActionRepository(
            new FixedDbContextProvider(readCtx),
            new ServiceCollection().BuildServiceProvider());

        var rows = await repository.GetByTaskIdAsync(task.Id);
        var empty = await repository.GetByTaskIdAsync(Guid.NewGuid());

        rows.Select(a => a.Id).ShouldBe([actions[0].Id, actions[1].Id, actions[2].Id]);
        rows[0].Detail.Json.ShouldContain("\"n\"");
        empty.ShouldBeEmpty();
    }

    /// <summary>
    /// vnext-client-sdk-core#60 item B: the metrics projection reads only the metadata columns for a
    /// SET of transition rows (payloads stay in the database, faulted Response is the one conditional
    /// exception), groups by owning transition, and carries the hook/order columns that separate a
    /// state's onEntry/onExit from the transition's onExecute.
    /// </summary>
    [Fact]
    public async Task GetMetricsRowsByTransitionIdsAsync_ProjectsMetricsColumnsGroupedByTransition()
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", "metrics-projection");
        var firstFiring = NewTransition(instance.Id, "to-review");
        var secondFiring = NewTransition(instance.Id, "to-review");
        var unrelated = NewTransition(instance.Id, "approve");

        InstanceTask entry, faulted, secondTask, unrelatedTask;
        await using (var ctx = CreateContext())
        {
            ctx.Instances.Add(instance);
            ctx.InstanceTransitions.AddRange(firstFiring, secondFiring, unrelated);

            entry = new InstanceTask(Guid.NewGuid(), firstFiring.Id, "crm-enrich", TaskTrigger.OnEntry, 0);
            faulted = new InstanceTask(Guid.NewGuid(), firstFiring.Id, "legacy-sync", TaskTrigger.OnExecute, 1);
            ctx.InstanceTasks.Add(entry);
            await Task.Delay(5);
            ctx.InstanceTasks.Add(faulted);
            secondTask = new InstanceTask(Guid.NewGuid(), secondFiring.Id, "risk-recalc", TaskTrigger.OnExecute, 1);
            ctx.InstanceTasks.Add(secondTask);
            unrelatedTask = new InstanceTask(Guid.NewGuid(), unrelated.Id, "noise", TaskTrigger.OnExecute, 1);
            ctx.InstanceTasks.Add(unrelatedTask);

            entry.Completed(JsonData.CreateFrom("""{"big":"payload"}"""), isBusinessSuccess: true);
            faulted.Faulted("connection refused");
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = CreateContext();
        var repository = CreateTaskRepository(readCtx);

        var rows = await repository.GetMetricsRowsByTransitionIdsAsync(
            new[] { firstFiring.Id, secondFiring.Id });

        // Only the two requested firings' tasks — the unrelated firing's task is excluded.
        rows.Count.ShouldBe(3);
        rows.ShouldNotContain(r => r.TaskKey == "noise");

        var firstFiringRows = rows.Where(r => r.TransitionId == firstFiring.Id).ToList();
        firstFiringRows.Count.ShouldBe(2);
        // Execution order (StartedAt) within the projection.
        firstFiringRows.Select(r => r.Id).ShouldBe([entry.Id, faulted.Id]);

        var entryRow = firstFiringRows.Single(r => r.TaskKey == "crm-enrich");
        entryRow.Hook.ShouldBe(TaskTrigger.OnEntry);
        entryRow.Order.ShouldBe(0);
        entryRow.Status.ShouldBe(TaskStatus.Completed);
        // A completed row's payload never leaves the database.
        entryRow.FaultedResponseJson.ShouldBeNull();

        var faultedRow = firstFiringRows.Single(r => r.TaskKey == "legacy-sync");
        faultedRow.Status.ShouldBe(TaskStatus.Faulted);
        faultedRow.FaultedResponseJson.ShouldNotBeNull();
        faultedRow.FaultedResponseJson.ShouldContain("connection refused");

        rows.Single(r => r.TransitionId == secondFiring.Id).TaskKey.ShouldBe("risk-recalc");

        // Empty input never touches the database.
        (await repository.GetMetricsRowsByTransitionIdsAsync(Array.Empty<Guid>())).ShouldBeEmpty();
    }

    private static InstanceTransition NewTransition(Guid instanceId, string key) =>
        InstanceTransition.Create(
            Guid.NewGuid(),
            instanceId,
            key,
            "InitialState",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}"));

    private async Task<InstanceTransition> SeedInstanceWithTransitionAsync(string key)
    {
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", key);
        var transition = InstanceTransition.Create(
            Guid.NewGuid(),
            instance.Id,
            "test-transition",
            "InitialState",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}"));

        await using var ctx = CreateContext();
        ctx.Instances.Add(instance);
        ctx.InstanceTransitions.Add(transition);
        await ctx.SaveChangesAsync();
        return transition;
    }

    private WorkflowDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new WorkflowDbContext(options, new StaticCurrentSchema("public"));
    }

    private static EfCoreInstanceTaskRepository CreateTaskRepository(WorkflowDbContext context) => new(
        new FixedDbContextProvider(context),
        new ServiceCollection().BuildServiceProvider(),
        Substitute.For<IDataSinkManager>());

    private sealed class FixedDbContextProvider(WorkflowDbContext context)
        : IAetherDbContextProvider<WorkflowDbContext>
    {
        public Task<WorkflowDbContext> GetDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(context);
    }
}
