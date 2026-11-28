using System;
using System.Collections.Generic;
using System.Dynamic;
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
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Coordinator;

using BBT.Workflow.Tasks.Coordinator;

/// <summary>
/// A task whose output is written to the instance's data commits its journal row in the same
/// transaction as the data row: the engine hands the journal write to
/// <see cref="IInstanceDataWriteService.AppendWithAsync"/>, which runs it inside the append's
/// row-lock transaction. Without a data write (or with a strategy that cannot join a caller's
/// transaction) the journal keeps its own commit.
/// </summary>
public sealed class TaskExecutionEngineJournalCoCommitTests
{
    private readonly ITaskExecutorRegistry _executorRegistry = Substitute.For<ITaskExecutorRegistry>();
    private readonly ITaskFactory _taskFactory = Substitute.For<ITaskFactory>();
    private readonly ITaskPersistenceStrategyFactory _strategyFactory = Substitute.For<ITaskPersistenceStrategyFactory>();
    private readonly IGuidGenerator _guidGenerator = Substitute.For<IGuidGenerator>();
    private readonly IInstanceDataWriteService _writeService = Substitute.For<IInstanceDataWriteService>();
    private readonly List<string> _events = [];

    public TaskExecutionEngineJournalCoCommitTests()
    {
        _guidGenerator.Create().Returns(_ => Guid.NewGuid());
        _writeService.AppendAsync(Arg.Any<Instance>(), Arg.Any<JsonData>(), Arg.Any<VersionStrategy?>(),
                Arg.Any<CancellationToken>(), Arg.Any<BBT.Workflow.Definitions.Workflow?>(), Arg.Any<bool>())
            .Returns(_ =>
            {
                _events.Add("append");
                return Task.FromResult<InstanceData?>(null);
            });
        _writeService.AppendWithAsync(Arg.Any<Instance>(), Arg.Any<JsonData>(), Arg.Any<VersionStrategy?>(),
                Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>(),
                Arg.Any<BBT.Workflow.Definitions.Workflow?>(), Arg.Any<bool>())
            .Returns(async call =>
            {
                _events.Add("append-begin");
                await call.ArgAt<Func<CancellationToken, Task>>(3)(CancellationToken.None);
                _events.Add("append-commit");
                return (InstanceData?)null;
            });
    }

    [Fact]
    public async Task DataOutput_WritesTheJournalInsideTheAppendTransaction()
    {
        var strategy = new RecordingStrategy(_events, completesInCallerTransaction: true);
        UseStrategy(strategy);
        ArrangeExecutor(withData: true);

        var result = await RunAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeTrue();
        _events.ShouldBe(["append-begin", "journal-in-transaction", "append-commit"]);
        strategy.Completed.ShouldNotBeNull();
        strategy.Completed.Status.ShouldBe(BBT.Workflow.Definitions.TaskStatus.Completed);
        strategy.Completed.BusinessStatus.ShouldBe(BusinessStatus.Success);
        strategy.Completed.Response.Json.ShouldContain("\"id\":42");
    }

    [Fact]
    public async Task NoDataOutput_KeepsTheJournalsOwnCommit()
    {
        var strategy = new RecordingStrategy(_events, completesInCallerTransaction: true);
        UseStrategy(strategy);
        ArrangeExecutor(withData: false);

        var result = await RunAsync();

        result.IsSuccess.ShouldBeTrue();
        _events.ShouldBe(["journal-own-commit"]);
    }

    [Fact]
    public async Task StrategyThatCannotJoinACallerTransaction_AppendsThenCommitsTheJournalSeparately()
    {
        var strategy = new RecordingStrategy(_events, completesInCallerTransaction: false);
        UseStrategy(strategy);
        ArrangeExecutor(withData: true);

        var result = await RunAsync();

        result.IsSuccess.ShouldBeTrue();
        _events.ShouldBe(["append", "journal-own-commit"]);
    }

    [Fact]
    public async Task FailedAppend_WritesNoJournalRow()
    {
        var strategy = new RecordingStrategy(_events, completesInCallerTransaction: true);
        UseStrategy(strategy);
        ArrangeExecutor(withData: true);
        _writeService.AppendWithAsync(Arg.Any<Instance>(), Arg.Any<JsonData>(), Arg.Any<VersionStrategy?>(),
                Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>(),
                Arg.Any<BBT.Workflow.Definitions.Workflow?>(), Arg.Any<bool>())
            .ThrowsAsync(new InvalidOperationException("lock timeout"));

        var result = await RunAsync();

        // As before the change: the append failed before the journal was reached, so the task is an
        // unhandled failure with no journal row, and a retry re-executes it.
        result.IsSuccess.ShouldBeFalse();
        _events.ShouldBeEmpty();
        strategy.Completed.ShouldBeNull();
    }

    // ---- helpers -------------------------------------------------------------------------------

    private void UseStrategy(ITaskPersistenceStrategy strategy) =>
        _strategyFactory.GetStrategy(Arg.Any<TaskExecutionOrigin>())
            .Returns(Result<ITaskPersistenceStrategy>.Ok(strategy));

    private void ArrangeExecutor(bool withData)
    {
        var task = WorkflowTaskFactory.CreateHttpTask("mock-api");
        _taskFactory.CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTask>.Ok(task));

        var executor = Substitute.For<ITaskExecutor>();
        executor.ExecuteAsync(Arg.Any<TaskExecutorContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => Result<StandardTaskResponse>.Ok(NewResponse(withData)));
        _executorRegistry.GetExecutor(Arg.Any<TaskType>()).Returns(Result<ITaskExecutor>.Ok(executor));
    }

    private static StandardTaskResponse NewResponse(bool withData)
    {
        dynamic data = new ExpandoObject();
        data.id = 42;
        return new StandardTaskResponse
        {
            IsSuccess = true,
            StatusCode = 200,
            Data = withData ? data : null,
            TaskType = "Http"
        };
    }

    private Task<Result<TasksExecutionResult>> RunAsync()
    {
        var engine = new TaskExecutionEngine(
            _executorRegistry, _taskFactory, _strategyFactory, _guidGenerator,
            new ErrorBoundaryResolver(NullLogger<ErrorBoundaryResolver>.Instance),
            new ErrorActionExecutor(NullLogger<ErrorActionExecutor>.Instance),
            new ExecutionErrorFactory(new ErrorNormalizer()),
            _writeService,
            NullLogger<TaskExecutionEngine>.Instance);

        var task = WorkflowTaskFactory.CreateHttpTask("mock-api");
        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key"))
            .Build();

        return engine.ExecuteAsync(
            OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty)),
            Guid.NewGuid(),
            TaskTrigger.OnExecute,
            TaskExecutionOrigin.Flow,
            scriptContext,
            CancellationToken.None);
    }

    private sealed class RecordingStrategy(List<string> events, bool completesInCallerTransaction) : ITaskPersistenceStrategy
    {
        public InstanceTask? Completed { get; private set; }

        public bool CompletesInCallerTransaction => completesInCallerTransaction;

        public bool CanHandle(TaskExecutionOrigin origin) => true;

        public Task<InstanceTask> HandleCreationAsync(InstanceTask instanceTask, TaskTrigger taskTrigger, int order,
            bool skipLookup = false, CancellationToken cancellationToken = default) => Task.FromResult(instanceTask);

        public Task HandleCompletionAsync(InstanceTask instanceTask, CancellationToken cancellationToken = default)
        {
            events.Add("journal-own-commit");
            Completed = instanceTask;
            return Task.CompletedTask;
        }

        public Task HandleCompletionInCallerTransactionAsync(InstanceTask instanceTask, CancellationToken cancellationToken = default)
        {
            events.Add("journal-in-transaction");
            Completed = instanceTask;
            return Task.CompletedTask;
        }
    }
}
