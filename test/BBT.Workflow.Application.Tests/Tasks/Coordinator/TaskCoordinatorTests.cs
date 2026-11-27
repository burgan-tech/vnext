using System;
using System.Collections.Generic;
using System.Linq;
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
                Arg.Any<TaskEngineExecutionOptions>(),
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

    /// <summary>
    /// vnext#1257: an unhandled task failure (no boundary) stops the hook — the tasks after it never
    /// run, and the failure travels up with its TaskError so the step faults.
    /// </summary>
    [Fact]
    public async Task ExecuteWithDetailsAsync_WhenATaskFailsUnhandled_StopsBeforeTheNextTask()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var definitions = SequentialDefinitions();
        ArrangeOutcome(engine, "first", call => TasksExecutionResult.Failure(call.Arg<OnExecuteTask>(), CreateError("first")));
        ArrangeOutcome(engine, "second", _ => TasksExecutionResult.Success([TaskExecutionSummary.Success("second", "Http")]));

        var result = await CreateCoordinator(engine, new ServiceCollection().BuildServiceProvider())
            .ExecuteWithDetailsAsync(definitions, Guid.NewGuid(), TaskTrigger.OnExecute, TaskExecutionOrigin.Flow, CreateContext());

        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.TaskError.ShouldNotBeNull();
        result.Value.FailedTask!.Task.Key.ShouldBe("first");
        await engine.DidNotReceive().ExecuteAsync(
            Arg.Is<OnExecuteTask>(t => t.Task.Key == "second"), Arg.Any<Guid?>(), Arg.Any<TaskTrigger>(),
            Arg.Any<TaskExecutionOrigin>(), Arg.Any<ScriptContext>(), Arg.Any<TaskEngineExecutionOptions>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// vnext#1268: the coordinator asks for an unhandled failure as an outcome, so the engine's
    /// ExecutionError — the script's stack trace included — reaches the task step and its incident,
    /// on the fresh-record path as well as the probing one.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteWithDetailsAsync_AsksTheEngineForUnhandledFailuresAsOutcomes(bool skipJournalProbe)
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        ArrangeOutcome(engine, "first", _ => TasksExecutionResult.Success([TaskExecutionSummary.Success("first", "Http")]));
        ArrangeOutcome(engine, "second", _ => TasksExecutionResult.Success([TaskExecutionSummary.Success("second", "Http")]));

        await CreateCoordinator(engine, new ServiceCollection().BuildServiceProvider())
            .ExecuteWithDetailsAsync(SequentialDefinitions(), Guid.NewGuid(), TaskTrigger.OnExecute,
                TaskExecutionOrigin.Flow, CreateContext(), [], skipJournalProbe);

        await engine.Received(2).ExecuteAsync(
            Arg.Any<OnExecuteTask>(), Arg.Any<Guid?>(), Arg.Any<TaskTrigger>(), Arg.Any<TaskExecutionOrigin>(),
            Arg.Any<ScriptContext>(),
            Arg.Is<TaskEngineExecutionOptions>(o => o.ReportUnhandledFailureAsOutcome && o.SkipJournalProbe == skipJournalProbe),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// vnext#1257: a failure the boundary handled with Ignore/Log does not stop the hook — the next
    /// task runs, and the hook as a whole succeeds while still reporting the failed task.
    /// </summary>
    [Fact]
    public async Task ExecuteWithDetailsAsync_WhenABoundaryHandlesAFailure_RunsTheNextTaskAndSucceeds()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var definitions = SequentialDefinitions();
        ArrangeOutcome(engine, "first", _ => HandledFailure("first"));
        ArrangeOutcome(engine, "second", _ => TasksExecutionResult.Success([TaskExecutionSummary.Success("second", "Http")]));

        var result = await CreateCoordinator(engine, new ServiceCollection().BuildServiceProvider())
            .ExecuteWithDetailsAsync(definitions, Guid.NewGuid(), TaskTrigger.OnExecute, TaskExecutionOrigin.Flow, CreateContext());

        var value = result.Value!;
        value.IsSuccess.ShouldBeTrue();
        value.HasFailedTasks.ShouldBeTrue();
        value.TaskError.ShouldBeNull();
        value.FailedTaskKeys.ShouldBe(["first"]);
        value.ExecutedTasks.Select(t => t.TaskKey).ShouldBe(["first", "second"]);
    }

    /// <summary>
    /// The parallel twin: a handled failure in one branch neither cancels nor fails its sibling.
    /// </summary>
    [Fact]
    public async Task ExecuteWithDetailsAsync_WhenAParallelBranchHasAHandledFailure_TheGroupSucceeds()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var definitions = new[]
        {
            OnExecuteTask.Create(1, WorkflowTaskFactory.CreateHttpTask("first"), ScriptCode.FromNative(string.Empty)),
            OnExecuteTask.Create(1, WorkflowTaskFactory.CreateHttpTask("second"), ScriptCode.FromNative(string.Empty))
        };
        ArrangeOutcome(engine, "first", _ => HandledFailure("first"));
        ArrangeOutcome(engine, "second", call =>
        {
            call.Arg<CancellationToken>().IsCancellationRequested.ShouldBeFalse();
            return TasksExecutionResult.Success([TaskExecutionSummary.Success("second", "Http")]);
        });
        var services = new ServiceCollection().AddSingleton(engine).BuildServiceProvider();

        var result = await CreateCoordinator(engine, services)
            .ExecuteWithDetailsAsync(definitions, Guid.NewGuid(), TaskTrigger.OnExecute, TaskExecutionOrigin.Flow, CreateContext());

        var value = result.Value!;
        value.IsSuccess.ShouldBeTrue();
        value.HasFailedTasks.ShouldBeTrue();
        value.FailedTaskKeys.ShouldBe(["first"]);
    }

    /// <summary>
    /// The plain overload (functions) collapses a hook whose only failures were handled to success.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenTheOnlyFailureWasHandled_ReturnsOk()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        ArrangeOutcome(engine, "first", _ => HandledFailure("first"));
        ArrangeOutcome(engine, "second", _ => TasksExecutionResult.Success([TaskExecutionSummary.Success("second", "Http")]));

        var result = await CreateCoordinator(engine, new ServiceCollection().BuildServiceProvider())
            .ExecuteAsync(SequentialDefinitions(), null, TaskTrigger.Extension, TaskExecutionOrigin.Function, CreateContext());

        result.IsSuccess.ShouldBeTrue();
    }

    private static OnExecuteTask[] SequentialDefinitions() =>
    [
        OnExecuteTask.Create(1, WorkflowTaskFactory.CreateHttpTask("first"), ScriptCode.FromNative(string.Empty)),
        OnExecuteTask.Create(2, WorkflowTaskFactory.CreateHttpTask("second"), ScriptCode.FromNative(string.Empty))
    ];

    private static void ArrangeOutcome(
        ITaskExecutionEngine engine,
        string taskKey,
        Func<NSubstitute.Core.CallInfo, TasksExecutionResult> outcome)
    {
        engine.ExecuteAsync(
                Arg.Is<OnExecuteTask>(task => task.Task.Key == taskKey),
                Arg.Any<Guid?>(),
                Arg.Any<TaskTrigger>(),
                Arg.Any<TaskExecutionOrigin>(),
                Arg.Any<ScriptContext>(),
                Arg.Any<TaskEngineExecutionOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(call => Result<TasksExecutionResult>.Ok(outcome(call)));
    }

    /// <summary>The engine's shape for a failure an Ignore/Log boundary let through.</summary>
    private static TasksExecutionResult HandledFailure(string taskKey) =>
        TasksExecutionResult.HandledFailure(
            TaskExecutionSummary.Failure(taskKey, "Http", "not found", 404, 0),
            CreateError(taskKey));

    private static ExecutionError CreateError(string taskKey) => new()
    {
        TaskKey = taskKey,
        TaskType = "Http",
        StatusCode = 404,
        ErrorMessage = "not found",
        NormalizedError = new NormalizedError { Code = $"Task:Http:{taskKey}:404", StatusCode = 404, Message = "not found" }
    };

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
