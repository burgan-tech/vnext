using System;
using System.Collections.Generic;
using System.Text.Json;
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
    private readonly IInstanceDataWriteService _instanceDataWriteService = Substitute.For<IInstanceDataWriteService>();

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
        _boundaryResolver,
        _actionExecutor,
        _errorFactory,
        _instanceDataWriteService,
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
        strategy.HandleCreationAsync(Arg.Any<InstanceTask>(), Arg.Any<TaskTrigger>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
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
    /// Arranges a fully successful execution: the factory yields <paramref name="factoryTask"/> and the
    /// executor returns a business-success response carrying data (so the data-apply path is reachable).
    /// </summary>
    private ITaskExecutor ArrangeSuccessfulExecution(WorkflowTask factoryTask)
    {
        _taskFactory.CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTask>.Ok(factoryTask));

        var executor = Substitute.For<ITaskExecutor>();
        executor.ExecuteAsync(Arg.Any<TaskExecutorContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<StandardTaskResponse>.Ok(new StandardTaskResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Data = new Dictionary<string, object> { ["result"] = "ok" }
            }));
        _executorRegistry.GetExecutor(Arg.Any<TaskType>())
            .Returns(Result<ITaskExecutor>.Ok(executor));

        UsePersistenceStrategy(new TrackingPersistenceStrategy(completionDelay: TimeSpan.Zero));
        return executor;
    }

    /// <summary>
    /// Additive guarantee: the options-less overload keeps writing instance data exactly as before.
    /// This is the control for <see cref="ExecuteAsync_WithSuppressDataApply_Should_Not_Write_Instance_Data"/> —
    /// without it, that test would also pass if the data-apply path were simply unreachable.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithoutOptions_Should_Still_Write_Instance_Data()
    {
        var task = WorkflowTaskFactory.CreateHttpTask("mock-api");
        ArrangeSuccessfulExecution(task);
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));

        var result = await CreateEngine().ExecuteAsync(
            onExecute, Guid.NewGuid(), TaskTrigger.OnEntry, TaskExecutionOrigin.Flow,
            CreateScriptContext(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _instanceDataWriteService.ReceivedWithAnyArgs(1)
            .AppendAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_WithSuppressDataApply_Should_Not_Write_Instance_Data()
    {
        var task = WorkflowTaskFactory.CreateHttpTask("mock-api");
        ArrangeSuccessfulExecution(task);
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));
        var options = new TaskEngineExecutionOptions { SuppressDataApply = true };

        var result = await CreateEngine().ExecuteAsync(
            onExecute, Guid.NewGuid(), TaskTrigger.OnEntry, TaskExecutionOrigin.Flow,
            CreateScriptContext(), options, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _instanceDataWriteService.DidNotReceiveWithAnyArgs()
            .AppendAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_WithPreparedTask_Should_Bypass_Factory_And_Use_JournalTaskKey()
    {
        // The definition-side task differs from the prepared instance, so a factory load would be visible.
        var definitionTask = WorkflowTaskFactory.CreateHttpTask("fan-out-docs");
        var prepared = WorkflowTaskFactory.CreateHttpTask("inner-http-task");

        var executor = Substitute.For<ITaskExecutor>();
        executor.ExecuteAsync(Arg.Any<TaskExecutorContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<StandardTaskResponse>.Ok(new StandardTaskResponse
            {
                IsSuccess = true,
                StatusCode = 200,
                Data = new Dictionary<string, object> { ["result"] = "ok" }
            }));
        _executorRegistry.GetExecutor(Arg.Any<TaskType>())
            .Returns(Result<ITaskExecutor>.Ok(executor));

        var strategy = Substitute.For<ITaskPersistenceStrategy>();
        strategy.HandleCreationAsync(Arg.Any<InstanceTask>(), Arg.Any<TaskTrigger>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<InstanceTask>());
        UsePersistenceStrategy(strategy);

        var onExecute = OnExecuteTask.Create(1, definitionTask, ScriptCode.FromNative(string.Empty));
        var options = new TaskEngineExecutionOptions
        {
            PreparedTask = prepared,
            JournalTaskKey = "fan-out-docs#3",
            SuppressDataApply = true
        };

        var result = await CreateEngine().ExecuteAsync(
            onExecute, Guid.NewGuid(), TaskTrigger.OnEntry, TaskExecutionOrigin.Flow,
            CreateScriptContext(), options, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _taskFactory.DidNotReceiveWithAnyArgs().CreateExecutionTaskAsync(default!, default);
        await strategy.Received().HandleCreationAsync(
            Arg.Is<InstanceTask>(t => t.TaskId == "fan-out-docs#3"),
            TaskTrigger.OnEntry, 1, Arg.Any<bool>(), Arg.Any<CancellationToken>());

        // The prepared instance — not a factory-loaded one — is what actually reached the executor.
        await executor.Received(1).ExecuteAsync(
            Arg.Is<TaskExecutorContext>(c => ReferenceEquals(c.Task, prepared)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WithCaptureResponse_Should_Return_StandardTaskResponse()
    {
        var task = WorkflowTaskFactory.CreateHttpTask("mock-api");
        ArrangeSuccessfulExecution(task);
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));
        var options = new TaskEngineExecutionOptions { CaptureResponse = true, SuppressDataApply = true };

        var result = await CreateEngine().ExecuteAsync(
            onExecute, Guid.NewGuid(), TaskTrigger.OnEntry, TaskExecutionOrigin.Flow,
            CreateScriptContext(), options, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Response.ShouldNotBeNull();
        result.Value.Response!.StatusCode.ShouldBe(200);
    }

    /// <summary>
    /// Without CaptureResponse the response is not surfaced — the capture is strictly opt-in.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithoutCaptureResponse_Should_Not_Expose_Response()
    {
        var task = WorkflowTaskFactory.CreateHttpTask("mock-api");
        ArrangeSuccessfulExecution(task);
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));

        var result = await CreateEngine().ExecuteAsync(
            onExecute, Guid.NewGuid(), TaskTrigger.OnEntry, TaskExecutionOrigin.Flow,
            CreateScriptContext(), CancellationToken.None);

        result.Value!.Response.ShouldBeNull();
    }

    /// <summary>
    /// The executor sees the real origin. Before Origin was threaded onto TaskExecutorContext the
    /// executor had no way to tell a Flow execution from an Extension/Function one.
    /// </summary>
    /// <remarks>
    /// Deliberately asserts a NON-Flow origin. Flow is what an un-passed Origin would fall back to,
    /// so a Flow-based assertion would stay green even if the engine stopped forwarding the value.
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_Should_Pass_Origin_To_ExecutorContext()
    {
        var task = WorkflowTaskFactory.CreateHttpTask("mock-api");
        var executor = ArrangeSuccessfulExecution(task);
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));

        await CreateEngine().ExecuteAsync(
            onExecute, null, TaskTrigger.Extension, TaskExecutionOrigin.Extension,
            CreateScriptContext(), CancellationToken.None);

        await executor.Received(1).ExecuteAsync(
            Arg.Is<TaskExecutorContext>(c => c.Origin == TaskExecutionOrigin.Extension),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Pins the PreparedTask retry lifetime documented on
    /// <see cref="TaskEngineExecutionOptions.PreparedTask"/>: the retry loop re-executes the very
    /// same instance on every attempt (the factory path would hand out a fresh one per attempt),
    /// and the factory stays out of the picture entirely across all attempts.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithPreparedTask_Should_Reuse_Same_Instance_Across_Retries()
    {
        var definitionTask = WorkflowTaskFactory.CreateHttpTask("fan-out-docs");
        var prepared = WorkflowTaskFactory.CreateHttpTask("inner-http-task");

        var seenTasks = new List<WorkflowTask>();
        var executor = Substitute.For<ITaskExecutor>();
        executor.ExecuteAsync(Arg.Any<TaskExecutorContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                seenTasks.Add(callInfo.Arg<TaskExecutorContext>().Task);
                return Result<StandardTaskResponse>.Fail(new Error("500", "transient boom"));
            });
        _executorRegistry.GetExecutor(Arg.Any<TaskType>())
            .Returns(Result<ITaskExecutor>.Ok(executor));

        UsePersistenceStrategy(new TrackingPersistenceStrategy(completionDelay: TimeSpan.Zero));

        // Wildcard Retry rule with a zero delay: exactly one retry, so two attempts in total.
        var errorBoundary = ErrorBoundary.WithRules(new ErrorHandlerRule
        {
            Action = ErrorAction.Retry,
            ErrorCodes = ["*"],
            Priority = 1,
            RetryPolicy = new RetryPolicy
            {
                MaxRetries = 1,
                UseJitter = false,
                InitialDelay = TimeSpan.Zero
            }
        });

        var onExecute = OnExecuteTask.Create(1, definitionTask, ScriptCode.FromNative(string.Empty), errorBoundary);
        var options = new TaskEngineExecutionOptions
        {
            PreparedTask = prepared,
            JournalTaskKey = "fan-out-docs#0",
            SuppressDataApply = true
        };

        await CreateEngine().ExecuteAsync(
            onExecute, Guid.NewGuid(), TaskTrigger.OnEntry, TaskExecutionOrigin.Flow,
            CreateScriptContext(), options, CancellationToken.None);

        seenTasks.Count.ShouldBe(2, "the wildcard Retry rule should produce one retry (two attempts)");
        seenTasks.ShouldAllBe(t => ReferenceEquals(t, prepared));
        await _taskFactory.DidNotReceiveWithAnyArgs().CreateExecutionTaskAsync(default!, default);
    }

    /// <summary>
    /// Production regression: <c>Npgsql.PostgresException 23505</c> on
    /// <c>UX_InstanceTasks_ExecutionKey</c> ("online_document_subprocess", FanOut-driven same-domain
    /// subprocesses with a per-item Retry boundary on a fresh transition record). Attempt 1 legitimately
    /// skips the journal idempotency probe (<see cref="TaskEngineExecutionOptions.SkipJournalProbe"/> is
    /// true for a transition record this pipeline run just inserted, so no prior row can exist). But the
    /// engine forwarded that SAME <see cref="TaskEngineExecutionOptions"/> instance, unchanged, into every
    /// retry attempt — so the retry also skipped the probe and re-inserted a journal row keyed on the same
    /// <c>ExecutionKey</c> (SHA256 of transitionId+taskId+trigger+order), colliding with attempt 1's
    /// row under the filtered unique index. Both <see cref="TaskEngineExecutionOptions.SkipJournalProbe"/> and
    /// <see cref="ITaskPersistenceStrategy.HandleCreationAsync"/>'s <c>skipLookup</c> parameter document
    /// that only the FIRST attempt may skip the probe — from the retry onward the probe must run, since
    /// it is what finds and reuses the previous attempt's row instead of inserting a duplicate.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenRetrying_Should_Restore_TheJournalProbe()
    {
        var task = WorkflowTaskFactory.CreateHttpTask("mock-api");
        _taskFactory.CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTask>.Ok(task));

        var executor = Substitute.For<ITaskExecutor>();
        var callCount = 0;
        executor.ExecuteAsync(Arg.Any<TaskExecutorContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1
                    ? Result<StandardTaskResponse>.Fail(new Error("500", "transient boom"))
                    : Result<StandardTaskResponse>.Ok(new StandardTaskResponse
                    {
                        IsSuccess = true,
                        StatusCode = 200,
                        Data = new Dictionary<string, object> { ["result"] = "ok" }
                    });
            });
        _executorRegistry.GetExecutor(Arg.Any<TaskType>())
            .Returns(Result<ITaskExecutor>.Ok(executor));

        var skipLookupCalls = new List<bool>();
        var strategy = Substitute.For<ITaskPersistenceStrategy>();
        strategy.HandleCreationAsync(Arg.Any<InstanceTask>(), Arg.Any<TaskTrigger>(), Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                skipLookupCalls.Add(callInfo.ArgAt<bool>(3));
                return Task.FromResult(callInfo.Arg<InstanceTask>());
            });
        UsePersistenceStrategy(strategy);

        // Wildcard Retry rule with no delay/jitter: exactly one retry (two attempts total),
        // without the test actually sleeping.
        var errorBoundary = ErrorBoundary.Builder()
            .OnErrorRetry(new RetryPolicy
            {
                MaxRetries = 1,
                InitialDelay = TimeSpan.FromMilliseconds(1),
                UseJitter = false
            })
            .Build();

        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty), errorBoundary);

        // The fresh-transition-record option: attempt 1 is allowed to skip the probe.
        var options = new TaskEngineExecutionOptions { SkipJournalProbe = true };

        var result = await CreateEngine().ExecuteAsync(
            onExecute, Guid.NewGuid(), TaskTrigger.OnExecute, TaskExecutionOrigin.Flow,
            CreateScriptContext(), options, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue("the retry should succeed on the second attempt");
        skipLookupCalls.Count.ShouldBe(2, "one journal-creation call per attempt");
        skipLookupCalls[0].ShouldBeTrue(
            "attempt 1 is the only one that can know no journal row exists yet - the perf win must survive");
        skipLookupCalls[1].ShouldBeFalse(
            "from the retry onward the probe must return - it is what finds and reuses attempt 1's row " +
            "instead of inserting a second one under the same ExecutionKey (UX_InstanceTasks_ExecutionKey)");
    }

    /// <summary>
    /// B8 regression: the audit request stored on <see cref="InstanceTask.Request"/> carries a task
    /// REFERENCE (key/version/domain/flow/type), not the full task definition. Guards against a
    /// regression back to embedding the whole <see cref="WorkflowTask"/> (including mapping/config)
    /// on every execution. <see cref="TaskExecutorContext.InputResponse"/> must still round-trip
    /// unchanged alongside it.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_Should_StoreTaskReference_NotDefinition_InRequestPayload()
    {
        var task = WorkflowTaskFactory.CreateHttpTask("mock-api");
        _taskFactory.CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTask>.Ok(task));

        // Real executors stamp InputResponse on the context (TaskExecutorBase); replicate that
        // here so the test can assert it round-trips unchanged into the audit payload.
        var executor = Substitute.For<ITaskExecutor>();
        executor.ExecuteAsync(Arg.Any<TaskExecutorContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callInfo.Arg<TaskExecutorContext>().InputResponse = new ScriptResponse { Key = "input-marker" };
                return Result<StandardTaskResponse>.Ok(new StandardTaskResponse
                {
                    IsSuccess = true,
                    StatusCode = 200,
                    Data = new Dictionary<string, object> { ["result"] = "ok" }
                });
            });
        _executorRegistry.GetExecutor(Arg.Any<TaskType>())
            .Returns(Result<ITaskExecutor>.Ok(executor));

        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));

        var strategy = new CapturingPersistenceStrategy();
        UsePersistenceStrategy(strategy);

        var result = await CreateEngine().ExecuteAsync(
            onExecute, Guid.NewGuid(), TaskTrigger.OnEntry, TaskExecutionOrigin.Flow,
            CreateScriptContext(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        strategy.CompletedInstanceTask.ShouldNotBeNull();

        using var requestDoc = JsonDocument.Parse(strategy.CompletedInstanceTask!.Request.Json);
        var root = requestDoc.RootElement;

        root.TryGetProperty("task", out var taskElement).ShouldBeTrue();
        taskElement.GetProperty("key").GetString().ShouldBe(task.Key);
        taskElement.GetProperty("version").GetString().ShouldBe(task.Version);
        taskElement.GetProperty("domain").GetString().ShouldBe(task.Domain);
        taskElement.GetProperty("flow").GetString().ShouldBe(task.Flow);
        taskElement.GetProperty("type").GetString().ShouldBe(task.GetTaskType().ToString());

        // Definition-only field (task config, incl. any mapping script code) must NOT leak
        // into the audit request — only the reference fields above are carried.
        taskElement.TryGetProperty("config", out _).ShouldBeFalse();

        // InputResponse still round-trips unchanged alongside the reference.
        root.TryGetProperty("inputResponse", out var inputResponseElement).ShouldBeTrue();
        inputResponseElement.GetProperty("key").GetString().ShouldBe("input-marker");
    }

    /// <summary>
    /// Production regression (UX_InstanceTasks_ExecutionKey 23505): the fresh-transition-record
    /// guarantee only covers the FIRST attempt. The error-aware retry loop re-executes the same
    /// (TransitionId, TaskId) identity, so every retry attempt must run the journal idempotency
    /// probe again (skipLookup=false) and reuse attempt #1's row — keeping the skip would insert
    /// a second row with the same ExecutionKey.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithFreshTransitionRecordAndRetry_ShouldProbeJournalOnRetryAttempts()
    {
        var task = WorkflowTaskFactory.CreateHttpTask("mock-api");
        _taskFactory.CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTask>.Ok(task));

        var executor = Substitute.For<ITaskExecutor>();
        executor.ExecuteAsync(Arg.Any<TaskExecutorContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<StandardTaskResponse>.Fail(new Error("500", "transient boom")));
        _executorRegistry.GetExecutor(Arg.Any<TaskType>())
            .Returns(Result<ITaskExecutor>.Ok(executor));

        var strategy = new SkipLookupRecordingPersistenceStrategy();
        UsePersistenceStrategy(strategy);

        var errorBoundary = ErrorBoundary.WithRules(new ErrorHandlerRule
        {
            Action = ErrorAction.Retry,
            ErrorCodes = ["*"],
            Priority = 1,
            RetryPolicy = new RetryPolicy { MaxRetries = 1, UseJitter = false, InitialDelay = TimeSpan.Zero }
        });
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty), errorBoundary);

        await CreateEngine().ExecuteAsync(
            onExecute, Guid.NewGuid(), TaskTrigger.OnEntry, TaskExecutionOrigin.Flow,
            CreateScriptContext(), TaskEngineExecutionOptions.FreshTransitionRecord, CancellationToken.None);

        // Attempt #1 may skip the probe (fresh record); every retry attempt must probe and reuse.
        strategy.SkipLookupCalls.Count.ShouldBe(2);
        strategy.SkipLookupCalls[0].ShouldBeTrue();
        strategy.SkipLookupCalls[1].ShouldBeFalse();
    }

    /// <summary>
    /// Test double recording the <c>skipLookup</c> argument of every creation persist, so the
    /// fresh-record retry regression test can pin the per-attempt probe decision.
    /// </summary>
    private sealed class SkipLookupRecordingPersistenceStrategy : ITaskPersistenceStrategy
    {
        public List<bool> SkipLookupCalls { get; } = [];

        public bool CanHandle(TaskExecutionOrigin origin) => true;

        public Task<InstanceTask> HandleCreationAsync(InstanceTask instanceTask, TaskTrigger taskTrigger, int order, bool skipLookup = false, CancellationToken cancellationToken = default)
        {
            SkipLookupCalls.Add(skipLookup);
            return Task.FromResult(instanceTask);
        }

        public Task HandleCompletionAsync(InstanceTask instanceTask, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
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

        public Task<InstanceTask> HandleCreationAsync(InstanceTask instanceTask, TaskTrigger taskTrigger, int order, bool skipLookup = false, CancellationToken cancellationToken = default)
            => Task.FromResult(instanceTask);

        public async Task HandleCompletionAsync(InstanceTask instanceTask, CancellationToken cancellationToken = default)
        {
            CompletionCallCount++;
            if (completionDelay > TimeSpan.Zero)
                await Task.Delay(completionDelay, cancellationToken);
            CompletionFinished = true;
        }
    }

    /// <summary>
    /// Test double that captures the <see cref="InstanceTask"/> instance passed to
    /// <see cref="HandleCompletionAsync"/>, so tests can inspect its <see cref="InstanceTask.Request"/>
    /// after a successful execution (the engine sets Request via <c>SetRequest</c> before completion).
    /// </summary>
    private sealed class CapturingPersistenceStrategy : ITaskPersistenceStrategy
    {
        public InstanceTask? CompletedInstanceTask { get; private set; }

        public bool CanHandle(TaskExecutionOrigin origin) => true;

        public Task<InstanceTask> HandleCreationAsync(InstanceTask instanceTask, TaskTrigger taskTrigger, int order, bool skipLookup = false, CancellationToken cancellationToken = default)
            => Task.FromResult(instanceTask);

        public Task HandleCompletionAsync(InstanceTask instanceTask, CancellationToken cancellationToken = default)
        {
            CompletedInstanceTask = instanceTask;
            return Task.CompletedTask;
        }
    }
}
