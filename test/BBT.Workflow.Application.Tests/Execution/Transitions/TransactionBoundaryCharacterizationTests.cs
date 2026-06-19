using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions;

/// <summary>
/// CHARACTERIZATION tests for the Phase 4 transaction-boundary refactor (Decision B).
///
/// These tests lock the CURRENT, observable behaviour at the pipeline-step level so that the later
/// Option-B cut (removing the single outer RequiresNew UoW in <c>TransitionRunner</c> and giving each
/// step its own short UoW) can be made safely. They deliberately assert <b>invariants that must
/// survive</b> the refactor, NOT implementation details that Option B will intentionally change.
///
/// Intentionally NOT asserted (these change under B — see docs/superpowers/phase4-design-note.md):
///   - that all writes commit only once at end-of-chain (B commits per step),
///   - that Busy is invisible until the final commit (B commits Busy early),
///   - that a transient fault re-runs the whole pipeline (B retries per step).
///
/// Coverage is at the step level (the meaningful unit boundary). True connection-pinning, Aether UoW
/// commit timing, cross-process crash-then-resume, and ChainReaper end-to-end are deferred to
/// integration tests (they require a real database).
/// </summary>
public class TransactionBoundaryCharacterizationTests
{
    private const string Domain = "test-domain";
    private const string WorkflowKey = "test-workflow";

    private static readonly Guid TransitionRecordId = Guid.NewGuid();

    // ---------------------------------------------------------------------------------------------
    // Invariant 1: successful transition reaches the target state and persists.
    // (ChangeStateStep is the authoritative state-change boundary; it must leave the instance in the
    //  target state and persist it regardless of how the transaction boundary is organized.)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ChangeState_OnSuccess_LeavesInstanceInTargetStateAndPersists()
    {
        // Arrange
        var instanceRepository = Substitute.For<IInstanceRepository>();
        instanceRepository
            .UpdateAsync(Arg.Any<Instance>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.ArgAt<Instance>(0)));

        var step = new ChangeStateStep(
            instanceRepository,
            Substitute.For<IWorkflowMetrics>(),
            Substitute.For<ILogger<ChangeStateStep>>());

        var context = CreateContext(out var workflow, out var instance);
        instance.ChangeState(workflow.GetState("state1").Value!);

        // Act: state1 -> state2
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert: final-state invariant.
        result.IsSuccess.ShouldBeTrue();
        context.Instance.GetCurrentState.ShouldBe("state2");

        // The state change is durably persisted (saveChanges:true). This MUST remain true under
        // Option B — only the *transaction that surrounds it* changes, not that it is persisted.
        await instanceRepository.Received().UpdateAsync(
            Arg.Is<Instance>(i => i.GetCurrentState == "state2"),
            true,
            Arg.Any<CancellationToken>());
    }

    // ---------------------------------------------------------------------------------------------
    // Invariant 2: a failure during the pipeline does not leave the instance falsely completed.
    // (An unhandled OnExecute task failure must surface as a Fail result and must NOT advance the
    //  instance to Completed / change its state. Completion only happens on the success path.)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnExecute_WhenTaskFailsUnhandled_DoesNotFalselyCompleteOrChangeState()
    {
        // Arrange
        var taskCoordinator = Substitute.For<ITaskCoordinatorExtended>();
        var instanceTaskRepository = Substitute.For<IInstanceTaskRepository>();
        instanceTaskRepository
            .GetSuccessfulTaskIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        // Infrastructure-level failure: the coordinator returns a failed Result, which
        // RunOnExecuteTasksStep propagates as a Fail StepOutcome (no state change, no completion).
        var unhandledError = Error.Failure("Instance:9001", "remote task blew up");
        taskCoordinator
            .ExecuteWithDetailsAsync(
                Arg.Any<IEnumerable<OnExecuteTask>>(),
                Arg.Any<Guid?>(),
                Arg.Any<TaskTrigger>(),
                Arg.Any<ScriptContext>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<TasksExecutionResult>.Fail(unhandledError));

        var step = CreateOnExecuteStep(taskCoordinator, instanceTaskRepository, out var instanceRepository);

        var context = CreateContextWithOnExecuteTasks(out var instance);
        var stateBefore = context.Instance.GetCurrentState;
        var statusBefore = context.Instance.Status;

        // Provide a pre-built ScriptContext so the step's railway path runs without hitting the
        // script-context factory builder.
        SeedScriptContext(context);

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert: the failure surfaces (no false success), and the instance is NOT completed and has
        // not advanced its state. THIS invariant must survive Option B.
        result.IsSuccess.ShouldBeFalse();
        context.Instance.IsCompleted.ShouldBeFalse();
        context.Instance.GetCurrentState.ShouldBe(stateBefore);
        context.Instance.Status.ShouldBe(statusBefore);
    }

    // ---------------------------------------------------------------------------------------------
    // Invariant 3: OnExecute remote tasks are invoked during the flow (the work actually happens).
    // (The same shape applies to OnExit/OnEntry — all three steps delegate to the same coordinator
    //  contract; OnExecute is the representative case characterized here.)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnExecute_InvokesRemoteTaskCoordinatorDuringFlow()
    {
        // Arrange
        var taskCoordinator = Substitute.For<ITaskCoordinatorExtended>();
        var instanceTaskRepository = Substitute.For<IInstanceTaskRepository>();
        instanceTaskRepository
            .GetSuccessfulTaskIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        taskCoordinator
            .ExecuteWithDetailsAsync(
                Arg.Any<IEnumerable<OnExecuteTask>>(),
                Arg.Any<Guid?>(),
                Arg.Any<TaskTrigger>(),
                Arg.Any<ScriptContext>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<TasksExecutionResult>.Ok(TasksExecutionResult.Success()));

        var step = CreateOnExecuteStep(taskCoordinator, instanceTaskRepository, out var instanceRepository);

        var context = CreateContextWithOnExecuteTasks(out _);
        SeedScriptContext(context);

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert: the remote coordinator was actually invoked, with the OnExecute trigger.
        result.IsSuccess.ShouldBeTrue();
        await taskCoordinator.Received(1).ExecuteWithDetailsAsync(
            Arg.Any<IEnumerable<OnExecuteTask>>(),
            Arg.Any<Guid?>(),
            TaskTrigger.OnExecute,
            Arg.Any<ScriptContext>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }

    // ---------------------------------------------------------------------------------------------
    // Invariant 4 (idempotency / resume): already-successful tasks are bypassed on re-run.
    // The step reads successfulTaskIds (via IInstanceTaskRepository.GetSuccessfulTaskIdsAsync) and
    // passes them to the coordinator as the bypass set. Under Option B this is precisely the
    // mechanism that makes a resumed run (after a per-step commit + crash) safe against duplicate
    // irreversible task side effects — so it MUST keep working.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnExecute_PassesSuccessfulTaskIdsToCoordinator_ForRetryBypass()
    {
        // Arrange: a previous attempt already completed "taskA" with business success.
        var alreadyDone = new List<string> { "taskA" };

        var taskCoordinator = Substitute.For<ITaskCoordinatorExtended>();
        var instanceTaskRepository = Substitute.For<IInstanceTaskRepository>();
        instanceTaskRepository
            .GetSuccessfulTaskIdsAsync(TransitionRecordId, Arg.Any<CancellationToken>())
            .Returns(alreadyDone);

        IEnumerable<string>? capturedBypass = null;
        taskCoordinator
            .ExecuteWithDetailsAsync(
                Arg.Any<IEnumerable<OnExecuteTask>>(),
                Arg.Any<Guid?>(),
                Arg.Any<TaskTrigger>(),
                Arg.Any<ScriptContext>(),
                Arg.Do<IEnumerable<string>>(ids => capturedBypass = ids.ToList()),
                Arg.Any<CancellationToken>())
            .Returns(Result<TasksExecutionResult>.Ok(TasksExecutionResult.Success()));

        var step = CreateOnExecuteStep(taskCoordinator, instanceTaskRepository, out _);

        var context = CreateContextWithOnExecuteTasks(out _);
        SeedScriptContext(context);

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert: the bypass list read from the repository is forwarded to the coordinator verbatim,
        // so the already-successful task is skipped rather than re-executed on this (resumed) run.
        result.IsSuccess.ShouldBeTrue();
        await instanceTaskRepository.Received(1)
            .GetSuccessfulTaskIdsAsync(TransitionRecordId, Arg.Any<CancellationToken>());
        capturedBypass.ShouldNotBeNull();
        capturedBypass!.ShouldContain("taskA");
    }

    // ---------------------------------------------------------------------------------------------
    // Resume-mechanism characterization (dormant write path documented in the design note):
    // ResumePointStepOrder is wired on the domain and read by TransitionExecutor, and ClearResumePoint
    // is the only production caller today. This locks the domain contract Option B will arm.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ResumePoint_DomainContract_SetAndClear_BehaveAsCheckpointStore()
    {
        var instance = Instance.Create(Guid.NewGuid(), WorkflowKey, "1.0.0");

        // Initially no in-flight checkpoint.
        instance.ResumePointStepOrder.ShouldBeNull();

        // A step can record its order as a durable resume point (armed by Option B per-step commits).
        instance.SetResumePoint(LifecycleOrder.OnExecute);
        instance.ResumePointStepOrder.ShouldBe(LifecycleOrder.OnExecute);

        // Finalize clears it so it never leaks into the next transition (current production behaviour).
        instance.ClearResumePoint();
        instance.ResumePointStepOrder.ShouldBeNull();
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static RunOnExecuteTasksStep CreateOnExecuteStep(
        ITaskCoordinatorExtended taskCoordinator,
        IInstanceTaskRepository instanceTaskRepository,
        out IInstanceRepository instanceRepository)
    {
        instanceRepository = Substitute.For<IInstanceRepository>();
        instanceRepository
            .UpdateAsync(Arg.Any<Instance>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.ArgAt<Instance>(0)));

        var scriptContextFactory = Substitute.For<IScriptContextFactory>();
        var runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();

        return new RunOnExecuteTasksStep(
            taskCoordinator,
            scriptContextFactory,
            instanceRepository,
            instanceTaskRepository,
            runtimeInfoProvider);
    }

    private static OnExecuteTask BuildOnExecuteTask(string key)
        => OnExecuteTask.Create(
            1,
            new Reference(key, Domain, "sys-tasks", "1.0.0"),
            ScriptCode.FromNative("return null;"),
            null);

    /// <summary>
    /// Seeds a pre-built ScriptContext into the context cache so RunOnExecuteTasksStep's
    /// GetOrBuildScriptContextAsync returns it without invoking the script-context factory builder.
    /// </summary>
    private static void SeedScriptContext(TransitionExecutionContext context)
    {
        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetWorkflow(context.Workflow)
            .SetInstance(context.Instance.CreateSnapshot())
            .Build();

        context.Cache["ScriptContext"] = scriptContext;
    }

    private static TransitionExecutionContext CreateContextWithOnExecuteTasks(out Instance instance)
    {
        var context = CreateContext(out var workflow, out instance);
        instance.ChangeState(workflow.GetState("state1").Value!);

        // Attach an OnExecute task to the transition so HasOnExecuteTasks() is true.
        context.Transition!.AddOnExecutionTask(BuildOnExecuteTask("taskA"));

        // The step reads the transition-record id from context items to scope the bypass query.
        context.Items["TransitionRecordId"] = TransitionRecordId;

        return context;
    }

    private static TransitionExecutionContext CreateContext(out Definitions.Workflow workflow, out Instance instance)
    {
        var instanceId = Guid.NewGuid();
        workflow = CreateWorkflow();
        instance = Instance.Create(instanceId, WorkflowKey, "1.0.0");
        var transition = Transition.Create("test-transition", "state1", "state2", TriggerType.Manual, "Patch");

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = Domain,
            WorkflowKey = WorkflowKey,
            TransitionKey = "test-transition",
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = workflow.GetState("state1").Value!,
            Transition = transition,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static Definitions.Workflow CreateWorkflow()
    {
        var json = """
                   {
                       "type": "F",
                       "timeout": null,
                       "labels": [],
                       "functions": [],
                       "features": [],
                       "states": [
                           { "key": "state1", "stateType": "Intermediate", "transitions": [] },
                           { "key": "state2", "stateType": "Intermediate", "transitions": [] }
                       ],
                       "sharedTransitions": [],
                       "extensions": [],
                       "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
                   }
                   """;

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference(WorkflowKey, Domain, "sys-flows", "1.0.0"));
        return workflow;
    }
}
