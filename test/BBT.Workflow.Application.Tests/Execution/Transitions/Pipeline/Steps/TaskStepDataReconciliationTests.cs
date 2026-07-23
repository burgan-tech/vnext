using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// Parameterized regression for the three task phases (OnExecute / OnExit / OnEntry):
/// the task coordinator executes exactly once per step regardless of reconciliation
/// retries (which live inside the reconciliation service), and an exhausted conflict
/// fails the step without persisting the instance.
/// </summary>
public sealed class TaskStepDataReconciliationTests
{
    [Theory]
    [InlineData(typeof(RunOnExecuteTasksStep))]
    [InlineData(typeof(RunOnExitTasksStep))]
    [InlineData(typeof(RunOnEntryTasksStep))]
    public async Task Conflict_then_success_should_not_reexecute_tasks(Type stepType)
    {
        var fixture = TaskStepFixture.Create(stepType);
        fixture.Applicator.ApplyAsync(default!, default!, default)
            .ReturnsForAnyArgs(Result.Ok());

        var result = await fixture.ExecuteAsync();

        result.IsSuccess.ShouldBeTrue();
        fixture.TaskCoordinatorExecutionCount.ShouldBe(1);
        await fixture.Applicator.ReceivedWithAnyArgs(1).ApplyAsync(default!, default!, default);
        await fixture.InstanceRepository.Received(1)
            .UpdateAsync(fixture.Instance, true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(typeof(RunOnExecuteTasksStep))]
    [InlineData(typeof(RunOnExitTasksStep))]
    [InlineData(typeof(RunOnEntryTasksStep))]
    public async Task Exhausted_conflict_should_fail_step_and_not_persist(Type stepType)
    {
        var fixture = TaskStepFixture.Create(stepType);
        fixture.Applicator.ApplyAsync(default!, default!, default)
            .ReturnsForAnyArgs(Result.Fail(
                WorkflowErrors.InstanceDataConcurrencyConflict(fixture.Instance.Id, 5)));

        var result = await fixture.ExecuteAsync();

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceDataConcurrencyConflict);
        fixture.TaskCoordinatorExecutionCount.ShouldBe(1);
        await fixture.InstanceRepository.DidNotReceiveWithAnyArgs()
            .UpdateAsync(default!, default, default);
    }

    [Theory]
    [InlineData(typeof(RunOnExecuteTasksStep), "OnExecute")]
    [InlineData(typeof(RunOnExitTasksStep), "OnExit")]
    [InlineData(typeof(RunOnEntryTasksStep), "OnEntry")]
    public async Task Step_should_tag_current_activity_with_pipeline_step_visible_at_apply_time(
        Type stepType, string expectedPhase)
    {
        var fixture = TaskStepFixture.Create(stepType);
        string? observedPhase = null;
        fixture.Applicator.ApplyAsync(default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                // Same-activity proof: the reconciliation service (behind the applicator)
                // reads Activity.Current — the step's tag must be visible right here.
                observedPhase = Activity.Current
                    ?.GetTagItem("workflow.pipeline.step")?.ToString();
                return Result.Ok();
            });

        using var activity = new Activity("pipeline-step").Start();

        var result = await fixture.ExecuteAsync();

        result.IsSuccess.ShouldBeTrue();
        observedPhase.ShouldBe(expectedPhase);
    }

    [Theory]
    [InlineData(typeof(RunOnExecuteTasksStep))]
    [InlineData(typeof(RunOnExitTasksStep))]
    [InlineData(typeof(RunOnEntryTasksStep))]
    public async Task Boundary_path_applicator_failure_should_surface_applicator_error_and_log_suppressed_task_error(
        Type stepType)
    {
        var fixture = TaskStepFixture.Create(stepType, taskFailsWithBoundary: true);
        fixture.Applicator.ApplyAsync(default!, default!, default)
            .ReturnsForAnyArgs(Result.Fail(
                WorkflowErrors.InstanceDataConcurrencyConflict(fixture.Instance.Id, 5)));

        var result = await fixture.ExecuteAsync();

        // The applicator failure preempts BoundaryOutcomeHandler and replaces the task error.
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.InstanceDataConcurrencyConflict);
        fixture.TaskCoordinatorExecutionCount.ShouldBe(1);
        await fixture.InstanceRepository.DidNotReceiveWithAnyArgs()
            .UpdateAsync(default!, default, default);

        // The suppressed original task error must be logged (WorkflowLogs EventId 10112).
        fixture.LoggedEventIds.ShouldContain(10112);
    }

    private sealed class TaskStepFixture
    {
        private int _taskCoordinatorExecutionCount;

        private readonly List<int> _loggedEventIds;

        private TaskStepFixture(
            ITransitionStep step,
            TransitionExecutionContext context,
            ITaskCoordinatorExtended taskCoordinator,
            IInstanceRepository instanceRepository,
            IScriptDataChangeApplicator applicator,
            OnExecuteTask task,
            bool taskFailsWithBoundary,
            List<int> loggedEventIds)
        {
            Step = step;
            Context = context;
            InstanceRepository = instanceRepository;
            Applicator = applicator;
            _loggedEventIds = loggedEventIds;

            taskCoordinator.ExecuteWithDetailsAsync(
                    default!, default, default, default, default!, default!, default)
                .ReturnsForAnyArgs(_ =>
                {
                    Interlocked.Increment(ref _taskCoordinatorExecutionCount);
                    return Result<TasksExecutionResult>.Ok(taskFailsWithBoundary
                        ? new TasksExecutionResult
                        {
                            IsSuccess = false,
                            HasFailedTasks = true,
                            FailedTaskKeys = ["task-1"],
                            FailedTask = task,
                            TaskError = new ExecutionError
                            {
                                TaskKey = "task-1",
                                TaskType = "Http",
                                StatusCode = 500,
                                ErrorMessage = "upstream failed",
                                NormalizedError = new NormalizedError { Code = "500" }
                            },
                            BoundaryAction = new BoundaryActionResult
                            {
                                Action = ErrorAction.Abort,
                                TransitionKey = "on-error"
                            }
                        }
                        : new TasksExecutionResult { IsSuccess = true });
                });
        }

        public ITransitionStep Step { get; }
        public TransitionExecutionContext Context { get; }
        public IInstanceRepository InstanceRepository { get; }
        public IScriptDataChangeApplicator Applicator { get; }
        public Instance Instance => Context.Instance;
        public int TaskCoordinatorExecutionCount => _taskCoordinatorExecutionCount;
        public IReadOnlyList<int> LoggedEventIds => _loggedEventIds;

        public Task<Result<StepOutcome>> ExecuteAsync() =>
            Step.ExecuteAsync(Context, CancellationToken.None);

        public static TaskStepFixture Create(Type stepType, bool taskFailsWithBoundary = false)
        {
            var taskCoordinator = Substitute.For<ITaskCoordinatorExtended>();
            var scriptContextFactory = Substitute.For<IScriptContextFactory>();
            var instanceRepository = Substitute.For<IInstanceRepository>();
            var instanceTaskRepository = Substitute.For<IInstanceTaskRepository>();
            var runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
            var applicator = Substitute.For<IScriptDataChangeApplicator>();

            var task = OnExecuteTask.Create(
                1,
                new Reference("task-1", "test-domain", "sys-tasks", "1.0.0"),
                ScriptCode.FromNative("// mapping"));

            var current = State.Create("state1", StateType.Intermediate, StateSubType.None, "Patch");
            current.AddOnExit(task);
            var target = State.Create("state2", StateType.Intermediate, StateSubType.None, "Patch");
            target.AddOnEntry(task);
            var transition = Transition.Create("test-transition", "state1", "state2", TriggerType.Manual, "Patch");
            transition.AddOnExecutionTask(task);

            var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0", "step-key");

            var context = new TransitionExecutionContext
            {
                InstanceId = instance.Id,
                Domain = "test-domain",
                WorkflowKey = "test-flow",
                TransitionKey = "test-transition",
                Trigger = TriggerType.Manual,
                CorrelationId = Guid.NewGuid().ToString("N"),
                ExecutionChainId = Guid.NewGuid().ToString("N"),
                RequestedAt = DateTimeOffset.UtcNow,
                Current = current,
                Target = target,
                Transition = transition,
                Instance = instance,
                TraceId = Guid.NewGuid().ToString("N"),
                SpanId = Guid.NewGuid().ToString("N")[..16]
            };

            // Pre-cache the ScriptContext so steps never invoke the factory builder chain.
            context.Cache["ScriptContext"] = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
                .SetRuntime(runtimeInfoProvider)
                .SetInstance(instance.CreateTrackedDataSnapshot())
                .Build();

            var loggedEventIds = new List<int>();
            ITransitionStep step = stepType switch
            {
                _ when stepType == typeof(RunOnExecuteTasksStep) => new RunOnExecuteTasksStep(
                    taskCoordinator, scriptContextFactory, instanceRepository,
                    instanceTaskRepository, runtimeInfoProvider, applicator,
                    new CapturingLogger<RunOnExecuteTasksStep>(loggedEventIds)),
                _ when stepType == typeof(RunOnExitTasksStep) => new RunOnExitTasksStep(
                    taskCoordinator, scriptContextFactory, instanceRepository,
                    instanceTaskRepository, runtimeInfoProvider, applicator,
                    new CapturingLogger<RunOnExitTasksStep>(loggedEventIds)),
                _ when stepType == typeof(RunOnEntryTasksStep) => new RunOnEntryTasksStep(
                    taskCoordinator, scriptContextFactory, instanceRepository,
                    instanceTaskRepository, runtimeInfoProvider, applicator,
                    new CapturingLogger<RunOnEntryTasksStep>(loggedEventIds)),
                _ => throw new ArgumentOutOfRangeException(nameof(stepType), stepType, "Unsupported step type.")
            };

            return new TaskStepFixture(
                step, context, taskCoordinator, instanceRepository, applicator,
                task, taskFailsWithBoundary, loggedEventIds);
        }
    }

    /// <summary>
    /// Minimal logger that records emitted EventIds for log-path assertions.
    /// </summary>
    private sealed class CapturingLogger<T>(List<int> sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (sink)
            {
                sink.Add(eventId.Id);
            }
        }
    }
}
