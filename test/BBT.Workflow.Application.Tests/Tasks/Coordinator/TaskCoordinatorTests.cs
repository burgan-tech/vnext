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
