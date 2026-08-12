using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Coordinator;

public sealed class TaskCoordinatorTests
{
    [Fact]
    public async Task ExecuteWithDetailsAsync_WhenParallelTasksCompleteOutOfOrder_MergesPrivateContextsInDefinitionOrder()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var firstTask = WorkflowTaskFactory.CreateHttpTask("first");
        var secondTask = WorkflowTaskFactory.CreateHttpTask("second");
        var definitions = new[]
        {
            OnExecuteTask.Create(1, firstTask, ScriptCode.FromNative(string.Empty)),
            OnExecuteTask.Create(1, secondTask, ScriptCode.FromNative(string.Empty))
        };
        var observedContexts = new List<ScriptContext>();

        engine.ExecuteAsync(
                Arg.Any<OnExecuteTask>(),
                Arg.Any<Guid?>(),
                Arg.Any<TaskTrigger>(),
                Arg.Any<TaskExecutionOrigin>(),
                Arg.Any<ScriptContext>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var definition = call.Arg<OnExecuteTask>();
                var branchContext = call.Arg<ScriptContext>();
                lock (observedContexts)
                    observedContexts.Add(branchContext);
                await Task.Delay(definition.Task.Key == "first" ? 50 : 1);
                branchContext.SetOutputResponse(definition.Task.Key, definition.Task.Key);
                return Result<TasksExecutionResult>.Ok(TasksExecutionResult.Success(
                    [TaskExecutionSummary.Success(definition.Task.Key, "Http")]));
            });

        var services = new ServiceCollection().AddSingleton(engine).BuildServiceProvider();
        var coordinator = CreateCoordinator(engine, services);
        var context = CreateContext();

        var result = await coordinator.ExecuteWithDetailsAsync(
            definitions,
            null,
            TaskTrigger.Extension,
            TaskExecutionOrigin.Extension,
            context);

        result.IsSuccess.ShouldBeTrue();
        observedContexts.Count.ShouldBe(2);
        observedContexts.ShouldAllBe(branch => !ReferenceEquals(branch, context));
        observedContexts[0].ShouldNotBeSameAs(observedContexts[1]);
        context.OutputResponse.Keys.ShouldBe(["first", "second"]);
    }

    [Fact]
    public async Task ExecuteWithDetailsAsync_WhenParallelBranchReturnsInfrastructureFailure_PropagatesFailure()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var firstTask = WorkflowTaskFactory.CreateHttpTask("first");
        var secondTask = WorkflowTaskFactory.CreateHttpTask("second");
        var definitions = new[]
        {
            OnExecuteTask.Create(1, firstTask, ScriptCode.FromNative(string.Empty)),
            OnExecuteTask.Create(1, secondTask, ScriptCode.FromNative(string.Empty))
        };

        engine.ExecuteAsync(
                Arg.Is<OnExecuteTask>(task => task.Task.Key == "first"),
                Arg.Any<Guid?>(),
                Arg.Any<TaskTrigger>(),
                Arg.Any<TaskExecutionOrigin>(),
                Arg.Any<ScriptContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<TasksExecutionResult>.Fail(Error.Failure("RemoteUnavailable", "remote unavailable")));
        engine.ExecuteAsync(
                Arg.Is<OnExecuteTask>(task => task.Task.Key == "second"),
                Arg.Any<Guid?>(),
                Arg.Any<TaskTrigger>(),
                Arg.Any<TaskExecutionOrigin>(),
                Arg.Any<ScriptContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<TasksExecutionResult>.Ok(TasksExecutionResult.Success(
                [TaskExecutionSummary.Success("second", "Http")])));

        var services = new ServiceCollection()
            .AddSingleton(engine)
            .BuildServiceProvider();
        var coordinator = CreateCoordinator(engine, services);
        var context = CreateContext();

        var result = await coordinator.ExecuteWithDetailsAsync(
            definitions,
            Guid.NewGuid(),
            TaskTrigger.OnExecute,
            TaskExecutionOrigin.Flow,
            context);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.FailedTaskKeys.ShouldContain("first");
    }

    [Fact]
    public async Task ExecuteWithDetailsAsync_InvokesGroupCheckpointAfterEachOrderGroup()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var definitions = new[]
        {
            OnExecuteTask.Create(1, WorkflowTaskFactory.CreateHttpTask("first"), ScriptCode.FromNative(string.Empty)),
            OnExecuteTask.Create(2, WorkflowTaskFactory.CreateHttpTask("second"), ScriptCode.FromNative(string.Empty)),
            OnExecuteTask.Create(3, WorkflowTaskFactory.CreateHttpTask("third"), ScriptCode.FromNative(string.Empty))
        };
        var executed = new List<string>();
        var checkpointsSeen = new List<int>();

        engine.ExecuteAsync(
                Arg.Any<OnExecuteTask>(),
                Arg.Any<Guid?>(),
                Arg.Any<TaskTrigger>(),
                Arg.Any<TaskExecutionOrigin>(),
                Arg.Any<ScriptContext>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var definition = call.Arg<OnExecuteTask>();
                executed.Add(definition.Task.Key);
                return Task.FromResult(Result<TasksExecutionResult>.Ok(TasksExecutionResult.Success(
                    [TaskExecutionSummary.Success(definition.Task.Key, "Http")])));
            });

        var services = new ServiceCollection().AddSingleton(engine).BuildServiceProvider();
        var coordinator = CreateCoordinator(engine, services);
        var context = CreateContext();

        var result = await coordinator.ExecuteWithDetailsAsync(
            definitions,
            null,
            TaskTrigger.OnExecute,
            TaskExecutionOrigin.Flow,
            context,
            completedTaskIds: [],
            groupCheckpoint: _ =>
            {
                // Snapshot how many tasks had completed when each checkpoint fired.
                checkpointsSeen.Add(executed.Count);
                return Task.CompletedTask;
            });

        result.IsSuccess.ShouldBeTrue();
        // One checkpoint per order group, fired AFTER that group's tasks completed:
        // after 'first' (1), after 'second' (2), after 'third' (3).
        checkpointsSeen.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task ExecuteWithDetailsAsync_FailingGroup_DoesNotInvokeItsCheckpoint()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var definitions = new[]
        {
            OnExecuteTask.Create(1, WorkflowTaskFactory.CreateHttpTask("first"), ScriptCode.FromNative(string.Empty)),
            OnExecuteTask.Create(2, WorkflowTaskFactory.CreateHttpTask("second"), ScriptCode.FromNative(string.Empty))
        };
        var checkpoints = 0;

        engine.ExecuteAsync(
                Arg.Any<OnExecuteTask>(),
                Arg.Any<Guid?>(),
                Arg.Any<TaskTrigger>(),
                Arg.Any<TaskExecutionOrigin>(),
                Arg.Any<ScriptContext>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<OnExecuteTask>().Task.Key == "first"
                ? Task.FromResult(Result<TasksExecutionResult>.Ok(TasksExecutionResult.Success(
                    [TaskExecutionSummary.Success("first", "Http")])))
                : Task.FromResult(Result<TasksExecutionResult>.Fail(
                    Error.Failure("Task:Http:second", "boom"))));

        var services = new ServiceCollection().AddSingleton(engine).BuildServiceProvider();
        var coordinator = CreateCoordinator(engine, services);

        var result = await coordinator.ExecuteWithDetailsAsync(
            definitions,
            null,
            TaskTrigger.OnExecute,
            TaskExecutionOrigin.Flow,
            CreateContext(),
            completedTaskIds: [],
            groupCheckpoint: _ =>
            {
                checkpoints++;
                return Task.CompletedTask;
            });

        // The first group persisted its work; the failing second group did not checkpoint.
        checkpoints.ShouldBe(1);
        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeFalse();
    }

    private static TaskCoordinator CreateCoordinator(
        ITaskExecutionEngine engine,
        ServiceProvider services) => new(
            engine,
            services.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IConditionEvaluator>(),
            Substitute.For<ITimerEvaluator>(),
            new ExecutionErrorFactory(new ErrorNormalizer()),
            NullLogger<TaskCoordinator>.Instance);

    private static ScriptContext CreateContext() =>
        new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .Build();
}
