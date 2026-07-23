using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
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

    private sealed class TaskStepFixture
    {
        private int _taskCoordinatorExecutionCount;

        private TaskStepFixture(
            ITransitionStep step,
            TransitionExecutionContext context,
            ITaskCoordinatorExtended taskCoordinator,
            IInstanceRepository instanceRepository,
            IScriptDataChangeApplicator applicator)
        {
            Step = step;
            Context = context;
            InstanceRepository = instanceRepository;
            Applicator = applicator;

            taskCoordinator.ExecuteWithDetailsAsync(
                    default!, default, default, default, default!, default!, default)
                .ReturnsForAnyArgs(_ =>
                {
                    Interlocked.Increment(ref _taskCoordinatorExecutionCount);
                    return Result<TasksExecutionResult>.Ok(new TasksExecutionResult { IsSuccess = true });
                });
        }

        public ITransitionStep Step { get; }
        public TransitionExecutionContext Context { get; }
        public IInstanceRepository InstanceRepository { get; }
        public IScriptDataChangeApplicator Applicator { get; }
        public Instance Instance => Context.Instance;
        public int TaskCoordinatorExecutionCount => _taskCoordinatorExecutionCount;

        public Task<Result<StepOutcome>> ExecuteAsync() =>
            Step.ExecuteAsync(Context, CancellationToken.None);

        public static TaskStepFixture Create(Type stepType)
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

            ITransitionStep step = stepType switch
            {
                _ when stepType == typeof(RunOnExecuteTasksStep) => new RunOnExecuteTasksStep(
                    taskCoordinator, scriptContextFactory, instanceRepository,
                    instanceTaskRepository, runtimeInfoProvider, applicator),
                _ when stepType == typeof(RunOnExitTasksStep) => new RunOnExitTasksStep(
                    taskCoordinator, scriptContextFactory, instanceRepository,
                    instanceTaskRepository, runtimeInfoProvider, applicator),
                _ when stepType == typeof(RunOnEntryTasksStep) => new RunOnEntryTasksStep(
                    taskCoordinator, scriptContextFactory, instanceRepository,
                    instanceTaskRepository, runtimeInfoProvider, applicator),
                _ => throw new ArgumentOutOfRangeException(nameof(stepType), stepType, "Unsupported step type.")
            };

            return new TaskStepFixture(step, context, taskCoordinator, instanceRepository, applicator);
        }
    }
}
