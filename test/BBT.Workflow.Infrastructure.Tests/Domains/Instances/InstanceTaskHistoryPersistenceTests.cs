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
/// Pins the reads behind the task-history and action-history system functions against a real
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
