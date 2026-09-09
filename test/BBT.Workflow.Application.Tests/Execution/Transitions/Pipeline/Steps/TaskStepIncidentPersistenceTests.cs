using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// Pins the ordering contract of the three task steps (<see cref="RunOnExecuteTasksStep"/> and its
/// OnEntry/OnExit twins): <b>an incident is recorded on the aggregate BEFORE the step saves it.</b>
/// </summary>
/// <remarks>
/// Why the order is load-bearing: an abort returns <c>Fail</c>, and the pipeline's fault path then
/// reloads the instance in its OWN unit of work and adds a fallback incident unless the committed
/// <c>HasActiveIncident</c> column already says one exists. Recording after the save left the flag
/// false at that instant, so one failure produced two rows — the boundary's verdict plus a bare
/// pipeline row — and the faulted instance's <c>incident.active</c> carried no boundary verdict.
/// The assertion is deliberately made from inside the <c>UpdateAsync</c> mock: it is the only place
/// that can observe the aggregate at save time rather than after the step returned.
/// </remarks>
public sealed class TaskStepIncidentPersistenceTests
{
    private const string Domain = "test-domain";
    private const string WorkflowKey = "test-workflow";
    private const string TaskKey = "failing-task";

    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly ITaskCoordinatorExtended _taskCoordinator = Substitute.For<ITaskCoordinatorExtended>();
    private readonly RunOnExecuteTasksStep _step;

    /// <summary>Incidents pending on the aggregate at the moment the step called UpdateAsync.</summary>
    private readonly List<IReadOnlyCollection<InstanceIncident>> _pendingAtSave = [];

    public TaskStepIncidentPersistenceTests()
    {
        _instanceRepository
            .UpdateAsync(Arg.Any<Instance>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var saved = call.ArgAt<Instance>(0);
                _pendingAtSave.Add(saved.GetPendingIncidents().ToList());
                return Task.FromResult(saved);
            });

        _step = new RunOnExecuteTasksStep(
            _taskCoordinator,
            Substitute.For<IScriptContextFactory>(),
            _instanceRepository,
            Substitute.For<IInstanceTaskRepository>(),
            Substitute.For<IRuntimeInfoProvider>(),
            Substitute.For<ILogger<RunOnExecuteTasksStep>>());
    }

    [Fact]
    public async Task BoundaryAbort_RecordsTheIncidentBeforeTheStepSavesTheInstance()
    {
        var context = CreateContext(out var instance);
        var error = CreateExecutionError();
        ArrangeTaskOutcome(TasksExecutionResult.WithBoundaryAction(
            context.Transition!.OnExecutionTasks.First(),
            BoundaryActionResult.Abort(
                Error.Failure("ErrorBoundaryAbort", "Error boundary aborted."),
                ErrorBoundaryLevel.Task,
                error)));

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // The step reports failure so the pipeline faults the instance.
        result.IsSuccess.ShouldBeFalse();

        // Exactly one save, and the incident was already on the aggregate when it happened.
        _pendingAtSave.Count.ShouldBe(1);
        var pending = _pendingAtSave[0];
        pending.Count.ShouldBe(1);
        pending.First().BoundaryAction.ShouldBe(nameof(ErrorAction.Abort));
        pending.First().BoundaryLevel.ShouldBe(nameof(ErrorBoundaryLevel.Task));
        pending.First().Task.ShouldBe(TaskKey);

        // The flag travels with that same save — it is what suppresses the fault path's fallback row.
        instance.HasActiveIncident.ShouldBeTrue();
    }

    [Fact]
    public async Task BoundaryActionWithATransition_AlsoRecordsBeforeSavingAndRoutesToFinalize()
    {
        var context = CreateContext(out _);
        var error = CreateExecutionError();
        ArrangeTaskOutcome(TasksExecutionResult.WithBoundaryAction(
            context.Transition!.OnExecutionTasks.First(),
            BoundaryActionResult.Transition("rollback-transition", ErrorAction.Rollback, ErrorBoundaryLevel.State, error)));

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.SkipToOrder.ShouldBe(LifecycleOrder.Finalize);
        context.Directives.NextTransition!.TransitionKey.ShouldBe("rollback-transition");

        _pendingAtSave.Count.ShouldBe(1);
        _pendingAtSave[0].Count.ShouldBe(1);
        _pendingAtSave[0].First().BoundaryAction.ShouldBe(nameof(ErrorAction.Rollback));
    }

    [Fact]
    public async Task AnUnhandledTaskFailure_RecordsTheIncidentBeforeSavingAndKeepsTheTaskError()
    {
        var context = CreateContext(out var instance);
        var error = CreateExecutionError();
        ArrangeTaskOutcome(TasksExecutionResult.Failure(context.Transition!.OnExecutionTasks.First(), error));

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(error.ToError().Code);

        _pendingAtSave.Count.ShouldBe(1);
        var pending = _pendingAtSave[0];
        pending.Count.ShouldBe(1);
        // No boundary resolved it, so the row carries the raw error rather than a verdict.
        pending.First().BoundaryAction.ShouldBeNull();
        pending.First().ErrorCode.ShouldBe(error.NormalizedError.Code);
        instance.HasActiveIncident.ShouldBeTrue();
    }

    [Fact]
    public async Task WhenTheIncidentSaveFails_TheStepStillFailsWithTheOriginalTaskError()
    {
        var context = CreateContext(out _);
        var error = CreateExecutionError();
        ArrangeTaskOutcome(TasksExecutionResult.Failure(context.Transition!.OnExecutionTasks.First(), error));

        _instanceRepository
            .UpdateAsync(Arg.Any<Instance>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<Task<Instance>>(_ => throw new InvalidOperationException("save exploded"));

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Best-effort save: the caller sees the task's own error, not a persistence error, and the
        // fault path's fallback incident keeps the failure visible because the flag never committed.
        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(error.ToError().Code);
    }

    private void ArrangeTaskOutcome(TasksExecutionResult outcome)
    {
        _taskCoordinator
            .ExecuteWithDetailsAsync(
                Arg.Any<IEnumerable<OnExecuteTask>>(),
                Arg.Any<Guid?>(),
                Arg.Any<TaskTrigger>(),
                Arg.Any<TaskExecutionOrigin>(),
                Arg.Any<ScriptContext>(),
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<bool>(),
                Arg.Any<Func<OnExecuteTask, TaskEngineExecutionOptions, TaskEngineExecutionOptions>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<TasksExecutionResult>.Ok(outcome)));
    }

    private static ExecutionError CreateExecutionError() => new()
    {
        TaskKey = TaskKey,
        TaskType = "HttpTask",
        StatusCode = 500,
        ErrorMessage = "downstream refused",
        NormalizedError = new NormalizedError
        {
            Code = "500",
            Layer = ErrorLayer.Task,
            StatusCode = 500,
            Message = "downstream refused"
        }
    };

    private TransitionExecutionContext CreateContext(out Instance instance)
    {
        var instanceId = Guid.NewGuid();
        var workflow = CreateWorkflow();
        instance = Instance.Create(instanceId, WorkflowKey, "1.0.0");
        instance.ChangeState(workflow.GetState("state1").Value!);

        var transition = CreateTransitionWithOneTask();

        var context = new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = Domain,
            WorkflowKey = WorkflowKey,
            TransitionKey = transition.Key,
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

        // A freshly inserted transition record: keeps the step off the task-journal lookup.
        context.Items[CreateTransitionRecordStep.TransitionRecordFreshKey] = true;

        // Pre-seed the cached ScriptContext so the step never reaches IScriptContextFactory.
        context.Cache["ScriptContext"] = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetWorkflow(workflow)
            .SetInstance(instance.CreateSnapshot())
            .Build();

        return context;
    }

    private static Transition CreateTransitionWithOneTask()
    {
        var json = $$"""
                     {
                         "key": "test-transition",
                         "from": "state1",
                         "target": "state2",
                         "triggerType": "Manual",
                         "versionStrategy": "Patch",
                         "labels": [],
                         "onExecutionTasks": [
                             {
                                 "order": 1,
                                 "task": { "key": "{{TaskKey}}", "domain": "{{Domain}}", "flow": "sys-tasks", "version": "1.0.0" },
                                 "mapping": { "code": "" }
                             }
                         ]
                     }
                     """;

        return JsonSerializer.Deserialize<Transition>(json, JsonOptions)!;
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

        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(json, JsonOptions)!;
        workflow.SetReference(new Reference(WorkflowKey, Domain, "sys-flows", "1.0.0"));
        return workflow;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
