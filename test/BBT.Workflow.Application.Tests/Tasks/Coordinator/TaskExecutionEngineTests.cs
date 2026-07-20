using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitoring;
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
/// Regression tests for <see cref="TaskExecutionEngine"/> covering the error/fallback path.
/// Guards issue #807: a task failure resolved by an Ignore boundary must not fault the instance,
/// and the failure-path completion persist must be fully awaited (no fire-and-forget) so it cannot
/// race the pipeline's next DbContext write.
/// </summary>
public sealed class TaskExecutionEngineTests
{
    private readonly ITaskExecutorRegistry _executorRegistry = Substitute.For<ITaskExecutorRegistry>();
    private readonly ITaskFactory _taskFactory = Substitute.For<ITaskFactory>();
    private readonly ITaskPersistenceStrategyFactory _persistenceStrategyFactory = Substitute.For<ITaskPersistenceStrategyFactory>();
    private readonly IGuidGenerator _guidGenerator = Substitute.For<IGuidGenerator>();
    private readonly IWorkflowMetrics _workflowMetrics = Substitute.For<IWorkflowMetrics>();

    // Real error-handling collaborators so boundary resolution is authentic.
    private readonly IErrorBoundaryResolver _boundaryResolver = new ErrorBoundaryResolver(NullLogger<ErrorBoundaryResolver>.Instance);
    private readonly IErrorActionExecutor _actionExecutor = new ErrorActionExecutor(NullLogger<ErrorActionExecutor>.Instance);
    private readonly IExecutionErrorFactory _errorFactory = new ExecutionErrorFactory(new ErrorNormalizer());

    public TaskExecutionEngineTests()
    {
        _guidGenerator.Create().Returns(_ => Guid.NewGuid());
    }

    private TaskExecutionEngine CreateEngine() => new(
        _executorRegistry,
        _taskFactory,
        _persistenceStrategyFactory,
        _guidGenerator,
        _workflowMetrics,
        _boundaryResolver,
        _actionExecutor,
        _errorFactory,
        NullLogger<TaskExecutionEngine>.Instance);

    private static ScriptContext CreateScriptContext()
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key");
        return new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(instance)
            .Build();
    }

    private void UsePersistenceStrategy(ITaskPersistenceStrategy strategy)
    {
        _persistenceStrategyFactory.GetStrategy(Arg.Any<TaskExecutionOrigin>())
            .Returns(Result<ITaskPersistenceStrategy>.Ok(strategy));
    }

    [Fact]
    public async Task ExecuteAsync_WhenFlowHasNoTransitionId_FailsBeforeRemoteExecution()
    {
        var engine = CreateEngine();
        var task = WorkflowTaskFactory.CreateHttpTask("mock-api");
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));

        var result = await engine.ExecuteAsync(
            onExecute,
            null,
            TaskTrigger.OnExecute,
            TaskExecutionOrigin.Flow,
            CreateScriptContext(),
            CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.TaskExecution);
        await _taskFactory.DidNotReceiveWithAnyArgs()
            .CreateExecutionTaskAsync(default!, default);
    }

    /// <summary>
    /// Issue #807 core regression: on a task failure the completion persist is awaited to
    /// completion before ExecuteAsync returns. With the previous fire-and-forget (`_ = ...`),
    /// control returned while the SaveChanges was still in flight, racing the pipeline's next
    /// DbContext write and tripping EF Core's ConcurrencyDetector.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenTaskFails_AwaitsFailurePersistBeforeReturning()
    {
        // Arrange: reach the failure branch via an executor-not-found result.
        var task = WorkflowTaskFactory.CreateHttpTask("mock-api");
        _taskFactory.CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTask>.Ok(task));
        _executorRegistry.GetExecutor(Arg.Any<TaskType>())
            .Returns(Result<ITaskExecutor>.Fail(new Error("500", "no executor registered")));

        var strategy = new TrackingPersistenceStrategy(completionDelay: TimeSpan.FromMilliseconds(75));
        UsePersistenceStrategy(strategy);

        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));
        var engine = CreateEngine();

        // Act
        await engine.ExecuteAsync(onExecute, Guid.NewGuid(), TaskTrigger.OnExecute, TaskExecutionOrigin.Flow, CreateScriptContext(), CancellationToken.None);

        // Assert: the failure persist finished before ExecuteAsync returned (no fire-and-forget).
        strategy.CompletionFinished.ShouldBeTrue(
            "the failure-path completion persist must be awaited before ExecuteAsync returns");
        strategy.CompletionCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenFailurePersistFails_ReturnsInfrastructureFailure()
    {
        var task = WorkflowTaskFactory.CreateHttpTask("mock-api");
        _taskFactory.CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTask>.Ok(task));
        _executorRegistry.GetExecutor(Arg.Any<TaskType>())
            .Returns(Result<ITaskExecutor>.Fail(new Error("500", "no executor registered")));

        var strategy = Substitute.For<ITaskPersistenceStrategy>();
        strategy.HandleCreationAsync(Arg.Any<InstanceTask>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<InstanceTask>());
        strategy.HandleCompletionAsync(Arg.Any<InstanceTask>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("journal write failed")));
        UsePersistenceStrategy(strategy);

        var errorBoundary = ErrorBoundary.WithRules(new ErrorHandlerRule
        {
            Action = ErrorAction.Ignore,
            ErrorCodes = ["*"],
            Priority = 1,
            LogOnly = true
        });
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty), errorBoundary);

        var result = await CreateEngine().ExecuteAsync(
            onExecute,
            Guid.NewGuid(),
            TaskTrigger.OnExecute,
            TaskExecutionOrigin.Flow,
            CreateScriptContext(),
            CancellationToken.None);

        result.IsSuccess.ShouldBeFalse("a failed journal write must not be reported as a persisted task failure");
    }

    /// <summary>
    /// Issue #807 behavioral guard: a code-less exception (no HTTP error code) does not match the
    /// Retry rule; it lands on the Ignore wildcard, runs zero retries, and the engine returns a
    /// success result (pipeline continues, instance not faulted).
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenTaskFailsWithNoErrorCode_ResolvesToIgnore_WithZeroRetries()
    {
        // Arrange: executor exists but the task fails at execution (mirrors an InputHandler failure,
        // which surfaces as a Result.Fail with no HTTP status code).
        var task = WorkflowTaskFactory.CreateHttpTask("mock-api");
        _taskFactory.CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTask>.Ok(task));

        var executor = Substitute.For<ITaskExecutor>();
        executor.ExecuteAsync(Arg.Any<TaskExecutorContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<StandardTaskResponse>.Fail(new Error("BusinessRule", "wrong operator cannot reject")));
        _executorRegistry.GetExecutor(Arg.Any<TaskType>())
            .Returns(Result<ITaskExecutor>.Ok(executor));

        UsePersistenceStrategy(new TrackingPersistenceStrategy(completionDelay: TimeSpan.Zero));

        // Issue's boundary config: Retry rule for HTTP codes + Ignore wildcard fallback.
        var errorBoundary = ErrorBoundary.WithRules(
            new ErrorHandlerRule
            {
                Action = ErrorAction.Retry,
                ErrorCodes = ["409", "500", "503", "504", "429", "408"],
                Priority = 1,
                RetryPolicy = new RetryPolicy { MaxRetries = 3, UseJitter = false }
            },
            new ErrorHandlerRule
            {
                Action = ErrorAction.Ignore,
                ErrorCodes = ["*"],
                Priority = 999,
                LogOnly = true
            });

        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty), errorBoundary);
        var engine = CreateEngine();

        // Act
        var result = await engine.ExecuteAsync(onExecute, Guid.NewGuid(), TaskTrigger.OnExecute, TaskExecutionOrigin.Flow, CreateScriptContext(), CancellationToken.None);

        // Assert: Ignore resolution => pipeline continues (not a hard failure/fault) with zero retries.
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value!.HasFailedTasks.ShouldBeTrue();
        await executor.Received(1).ExecuteAsync(Arg.Any<TaskExecutorContext>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Test double that records whether the completion persist ran to completion, with an
    /// optional delay so an un-awaited (fire-and-forget) call is observably incomplete when
    /// ExecuteAsync returns.
    /// </summary>
    private sealed class TrackingPersistenceStrategy(TimeSpan completionDelay) : ITaskPersistenceStrategy
    {
        public bool CompletionFinished { get; private set; }
        public int CompletionCallCount { get; private set; }

        public bool CanHandle(TaskExecutionOrigin origin) => true;

        public Task<InstanceTask> HandleCreationAsync(InstanceTask instanceTask, CancellationToken cancellationToken = default)
            => Task.FromResult(instanceTask);

        public async Task HandleCompletionAsync(InstanceTask instanceTask, CancellationToken cancellationToken = default)
        {
            CompletionCallCount++;
            if (completionDelay > TimeSpan.Zero)
                await Task.Delay(completionDelay, cancellationToken);
            CompletionFinished = true;
        }
    }
}
