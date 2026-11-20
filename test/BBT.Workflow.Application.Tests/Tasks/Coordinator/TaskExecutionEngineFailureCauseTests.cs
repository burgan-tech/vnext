using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Factory;
using BBT.Workflow.Tasks.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Coordinator;

using BBT.Workflow.Tasks.Coordinator;

/// <summary>
/// vnext#1268: a task script that throws leaves its exception on the script context, and the engine
/// turns it into the <see cref="ExecutionError.StackTrace"/> the incident is written from. What a
/// client sees — the error's code, message and status — must stay exactly what it was: on the
/// coordinator's path that is the error the coordinator used to re-derive from a failed Result.
/// </summary>
public sealed class TaskExecutionEngineFailureCauseTests
{
    private const string TaskKey = "mock-api";

    private readonly ITaskExecutorRegistry _executorRegistry = Substitute.For<ITaskExecutorRegistry>();
    private readonly ITaskFactory _taskFactory = Substitute.For<ITaskFactory>();
    private readonly ITaskPersistenceStrategyFactory _persistenceStrategyFactory = Substitute.For<ITaskPersistenceStrategyFactory>();
    private readonly IGuidGenerator _guidGenerator = Substitute.For<IGuidGenerator>();
    private readonly ExecutionErrorFactory _errorFactory = new(new ErrorNormalizer());
    private readonly ITaskExecutor _executor = Substitute.For<ITaskExecutor>();
    private readonly WorkflowTask _task = WorkflowTaskFactory.CreateHttpTask(TaskKey);

    private static readonly TaskEngineExecutionOptions Coordinated = new() { ReportUnhandledFailureAsOutcome = true };

    public TaskExecutionEngineFailureCauseTests()
    {
        _guidGenerator.Create().Returns(_ => Guid.NewGuid());
        _taskFactory.CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTask>.Ok(_task));
        _executorRegistry.GetExecutor(Arg.Any<TaskType>()).Returns(Result<ITaskExecutor>.Ok(_executor));
        _persistenceStrategyFactory.GetStrategy(Arg.Any<TaskExecutionOrigin>())
            .Returns(Result<ITaskPersistenceStrategy>.Ok(new NoOpPersistenceStrategy()));
    }

    private TaskExecutionEngine CreateEngine() => new(
        _executorRegistry,
        _taskFactory,
        _persistenceStrategyFactory,
        _guidGenerator,
        new ErrorBoundaryResolver(NullLogger<ErrorBoundaryResolver>.Instance),
        new ErrorActionExecutor(NullLogger<ErrorActionExecutor>.Instance),
        _errorFactory,
        Substitute.For<IInstanceDataWriteService>(),
        NullLogger<TaskExecutionEngine>.Instance);

    private static ScriptContext CreateScriptContext() =>
        new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key"))
            .Build();

    /// <summary>A script failure the way the executors report one: inner cause, outer wrapper.</summary>
    private static InvalidOperationException ScriptException() =>
        new("the input handler could not read the amount", new FormatException("not-a-number is not an integer"));

    /// <summary>The executor's input mapping throws: Result.Fail, cause left on the script context.</summary>
    private void ArrangeInputMappingThrows(Exception exception) =>
        _executor.ExecuteAsync(Arg.Any<TaskExecutorContext>(), Arg.Any<CancellationToken>())
            .Returns(call => Result<StandardTaskResponse>.Fail(ScriptDiagnostics.TaskScriptError(
                call.Arg<TaskExecutorContext>().ScriptContext, exception, "Http task input handler failed")));

    /// <summary>A script task's output handler throws: a failed response (500), cause left on the context.</summary>
    private void ArrangeOutputHandlerThrows(Exception exception) =>
        _executor.ExecuteAsync(Arg.Any<TaskExecutorContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var error = ScriptDiagnostics.TaskScriptError(
                    call.Arg<TaskExecutorContext>().ScriptContext, exception, "Script output handler failed");
                return Result<StandardTaskResponse>.Ok(new StandardTaskResponse
                {
                    IsSuccess = false,
                    StatusCode = 500,
                    ErrorMessage = error.Message,
                    Data = new Dictionary<string, object>()
                });
            });

    private Task<Result<TasksExecutionResult>> RunAsync(
        ScriptContext context, TaskEngineExecutionOptions options, ErrorBoundary? boundary = null) =>
        CreateEngine().ExecuteAsync(
            OnExecuteTask.Create(1, _task, ScriptCode.FromNative(string.Empty), boundary),
            Guid.NewGuid(), TaskTrigger.OnExecute, TaskExecutionOrigin.Flow, context, options, CancellationToken.None);

    /// <summary>What the coordinator builds from a failed Result — the shape before vnext#1268.</summary>
    private ExecutionError AsTheCoordinatorRederivesIt(Result<TasksExecutionResult> failed)
    {
        failed.IsSuccess.ShouldBeFalse();
        return _errorFactory.CreateFromError(failed.Error, TaskKey, "Unknown", 0);
    }

    private static void ShouldMatchVisibly(ExecutionError actual, ExecutionError expected)
    {
        actual.NormalizedError.Code.ShouldBe(expected.NormalizedError.Code);
        actual.NormalizedError.Layer.ShouldBe(expected.NormalizedError.Layer);
        actual.ErrorMessage.ShouldBe(expected.ErrorMessage);
        actual.StatusCode.ShouldBe(expected.StatusCode);
    }

    [Fact]
    public async Task UnhandledInputMappingFailure_ReachesTheCoordinatorWithItsStackTrace_AndAnUnchangedError()
    {
        var exception = ScriptException();
        ArrangeInputMappingThrows(exception);
        var context = CreateScriptContext();

        var outcome = await RunAsync(context, Coordinated);
        var legacy = await RunAsync(CreateScriptContext(), TaskEngineExecutionOptions.Default);

        outcome.IsSuccess.ShouldBeTrue();
        var error = outcome.Value!.TaskError.ShouldNotBeNull();
        outcome.Value.BoundaryAction.ShouldBeNull();
        outcome.Value.IsSuccess.ShouldBeFalse();
        var trace = error.StackTrace.ShouldNotBeNull();
        trace.ShouldBe(exception.ToString());
        trace.ShouldContain(nameof(FormatException));
        ShouldMatchVisibly(error, AsTheCoordinatorRederivesIt(legacy));
        error.NormalizedError.Code.ShouldBe($"Task:Http:{TaskKey}");
        context.FailureCause.ShouldBeNull("the engine takes the cause, so it cannot reach the next task");
    }

    [Fact]
    public async Task WithoutTheOption_AnUnhandledFailureIsStillAFailedResult()
    {
        // FanOut items and agent tools read the failed Result; their contract does not move.
        ArrangeInputMappingThrows(ScriptException());

        var result = await RunAsync(CreateScriptContext(), TaskEngineExecutionOptions.Default);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe($"Task:Http:{TaskKey}");
    }

    [Fact]
    public async Task OutputHandlerFailure_WithNoBoundaryAnywhere_KeepsItsStackTrace()
    {
        var exception = ScriptException();
        ArrangeOutputHandlerThrows(exception);

        var result = await RunAsync(CreateScriptContext(), Coordinated);

        var error = result.Value!.TaskError.ShouldNotBeNull();
        error.StatusCode.ShouldBe(500);
        error.NormalizedError.Code.ShouldBe($"Task:Http:{TaskKey}:500");
        error.StackTrace.ShouldBe(exception.ToString());
    }

    [Fact]
    public async Task OutputHandlerFailure_WithABoundaryThatDoesNotMatch_KeepsItsStackTrace_AndAnUnchangedError()
    {
        var exception = ScriptException();
        ArrangeOutputHandlerThrows(exception);
        var noMatch = ErrorBoundary.WithRules(new ErrorHandlerRule { Action = ErrorAction.Abort, ErrorCodes = ["404"], Priority = 1 });

        var outcome = await RunAsync(CreateScriptContext(), Coordinated, noMatch);
        var legacy = await RunAsync(CreateScriptContext(), TaskEngineExecutionOptions.Default, noMatch);

        var error = outcome.Value!.TaskError.ShouldNotBeNull();
        outcome.Value.BoundaryAction.ShouldBeNull();
        error.StackTrace.ShouldBe(exception.ToString());
        ShouldMatchVisibly(error, AsTheCoordinatorRederivesIt(legacy));
    }

    [Fact]
    public async Task InputMappingFailure_AbortedByABoundary_KeepsItsStackTrace()
    {
        var exception = ScriptException();
        ArrangeInputMappingThrows(exception);
        var abort = ErrorBoundary.WithRules(new ErrorHandlerRule { Action = ErrorAction.Abort, Priority = 999 });

        var result = await RunAsync(CreateScriptContext(), Coordinated, abort);

        result.Value!.BoundaryAction.ShouldNotBeNull();
        result.Value.TaskError.ShouldNotBeNull().StackTrace.ShouldBe(exception.ToString());
    }

    [Fact]
    public async Task ExceptionEscapingTheExecutor_ReachesTheCoordinatorWithItsStackTrace_AndAnUnchangedError()
    {
        var exception = ScriptException();
        _executor.ExecuteAsync(Arg.Any<TaskExecutorContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<StandardTaskResponse>>>(call =>
            {
                // A script failed earlier in the same run, then the executor itself threw.
                call.Arg<TaskExecutorContext>().ScriptContext.FailureCause = new FormatException("earlier in the run");
                throw exception;
            });
        var context = CreateScriptContext();

        var outcome = await RunAsync(context, Coordinated);
        var legacy = await RunAsync(CreateScriptContext(), TaskEngineExecutionOptions.Default);

        var error = outcome.Value!.TaskError.ShouldNotBeNull();
        error.StackTrace.ShouldBe(exception.ToString());
        ShouldMatchVisibly(error, AsTheCoordinatorRederivesIt(legacy));
        context.FailureCause.ShouldBeNull("taken even when the executor throws, so it cannot reach the next task");
    }

    [Fact]
    public async Task FailureWithoutAThrownScript_CarriesNoStackTrace()
    {
        _executor.ExecuteAsync(Arg.Any<TaskExecutorContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<StandardTaskResponse>.Ok(new StandardTaskResponse
            {
                IsSuccess = false,
                StatusCode = 503,
                ErrorMessage = "service unavailable",
                Data = new Dictionary<string, object>()
            }));

        var result = await RunAsync(CreateScriptContext(), Coordinated);

        result.Value!.TaskError.ShouldNotBeNull().StackTrace.ShouldBeNull();
    }

    [Fact]
    public async Task ACauseLeftOnTheContext_DoesNotOutliveTheRun()
    {
        // Whatever a run leaves on the context — even a run that then succeeds — the next task on the
        // same context must not inherit it.
        _executor.ExecuteAsync(Arg.Any<TaskExecutorContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<TaskExecutorContext>().ScriptContext.FailureCause = ScriptException();
                return Result<StandardTaskResponse>.Ok(new StandardTaskResponse { IsSuccess = true, StatusCode = 200 });
            });
        var context = CreateScriptContext();

        var result = await RunAsync(context, Coordinated);

        result.Value!.IsSuccess.ShouldBeTrue();
        context.FailureCause.ShouldBeNull();
    }

    private sealed class NoOpPersistenceStrategy : ITaskPersistenceStrategy
    {
        public bool CanHandle(TaskExecutionOrigin origin) => true;

        public Task<InstanceTask> HandleCreationAsync(InstanceTask instanceTask, TaskTrigger taskTrigger, int order,
            bool skipLookup = false, CancellationToken cancellationToken = default) => Task.FromResult(instanceTask);

        public Task HandleCompletionAsync(InstanceTask instanceTask, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
