using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// Unit tests for <see cref="ScheduleTransitionsStep"/>. The step runs AFTER
/// RunAutomaticTransitionsStep; when that step selected a winner (Directives.NextTransition
/// is set) the instance is about to leave the state, so arming its timers would be pure
/// waste — the chained hop's CancelScheduledJobsStep would tear them down immediately.
/// </summary>
public class ScheduleTransitionsStepTests
{
    private const string Domain = "test-domain";
    private const string WorkflowKey = "test-workflow";

    private readonly IBackgroundJobService _backgroundJobService = Substitute.For<IBackgroundJobService>();
    private readonly ITaskTimerService _taskTimerService = Substitute.For<ITaskTimerService>();
    private readonly IScriptContextFactory _scriptContextFactory = Substitute.For<IScriptContextFactory>();
    private readonly IInstanceJobRepository _jobRepository = Substitute.For<IInstanceJobRepository>();
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IRuntimeInfoProvider _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();

    private ScheduleTransitionsStep CreateStep() => new(
        _backgroundJobService,
        _taskTimerService,
        _scriptContextFactory,
        _jobRepository,
        _instanceRepository,
        NullLogger<ScheduleTransitionsStep>.Instance,
        _runtimeInfoProvider);

    [Fact]
    public void Order_ShouldBeSchedule()
    {
        CreateStep().Order.ShouldBe(LifecycleOrder.Schedule);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNextTransitionAlreadySelected_ShouldSkipArmingEntirely()
    {
        // Arrange: target state HAS scheduled transitions, but the Auto step (order 80,
        // runs before this step at 90) already selected a winner.
        var context = CreateContextWithScheduledTarget();
        context.Directives.RequestNextTransition(new NextTransitionRequest("auto-next", "auto"));

        // Act
        var result = await CreateStep().ExecuteAsync(context, CancellationToken.None);

        // Assert: no-op outcome, and NOTHING was armed or persisted.
        result.IsSuccess.ShouldBeTrue();
        result.Value!.StopPipeline.ShouldBeFalse();
        _scriptContextFactory.ReceivedCalls().ShouldBeEmpty();
        _taskTimerService.ReceivedCalls().ShouldBeEmpty();
        _backgroundJobService.ReceivedCalls().ShouldBeEmpty();
        _jobRepository.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoNextTransition_ShouldProceedToArm()
    {
        // Arrange: same state, but no winner was selected — arming must proceed.
        // We assert the step ENTERS the arming path (script context build is the first
        // side effect); the full arm/persist chain is covered by integration tests.
        var context = CreateContextWithScheduledTarget();
        context.Directives.NextTransition.ShouldBeNull();

        // Act — the substituted builder chain is unconfigured beyond NewBuilder, so
        // BuildAsync resolves to a null ScriptContext and the downstream arming chain
        // throws (NullReferenceException). That is fine and expected: this test only
        // proves the guard did NOT short-circuit before the factory was invoked — the
        // full arm/persist chain is covered by integration tests.
        try
        {
            await CreateStep().ExecuteAsync(context, CancellationToken.None);
        }
        catch
        {
            // Downstream failure past the guard is irrelevant here; see comment above.
        }

        // Assert
        _scriptContextFactory.Received(1).NewBuilder(_instanceRepository);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoScheduledTransitions_ShouldContinueNoWork()
    {
        var context = CreateContextWithPlainTarget();

        var result = await CreateStep().ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _scriptContextFactory.ReceivedCalls().ShouldBeEmpty();
        _backgroundJobService.ReceivedCalls().ShouldBeEmpty();
    }

    private TransitionExecutionContext CreateContextWithScheduledTarget()
        => CreateContext(CreateWorkflow(withScheduled: true));

    private TransitionExecutionContext CreateContextWithPlainTarget()
        => CreateContext(CreateWorkflow(withScheduled: false));

    private static TransitionExecutionContext CreateContext(Definitions.Workflow workflow)
    {
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, WorkflowKey, "1.0.0");
        instance.ChangeState(workflow.GetState("state1").Value!);

        var context = new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = Domain,
            WorkflowKey = WorkflowKey,
            TransitionKey = "go",
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = workflow.GetState("state1").Value!,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
        // ChangeStateStep normally sets Target; the epilogue reads it.
        context.Target = workflow.GetState("state2").Value!;
        return context;
    }

    private static Definitions.Workflow CreateWorkflow(bool withScheduled)
    {
        // state2 is the entered target. In the scheduled variant it carries one
        // Scheduled-trigger transition with a duration timer.
        var scheduledTransitionJson = withScheduled
            ? """
              {
                  "key": "timeout-check",
                  "target": "state1",
                  "triggerType": "Scheduled",
                  "versionStrategy": "Patch",
                  "labels": [],
                  "onExecutionTasks": [],
                  "view": null,
                  "timer": { "type": "duration", "duration": "PT5M" }
              }
              """
            : null;

        var json = $$"""
                   {
                       "type": "F",
                       "timeout": null,
                       "labels": [],
                       "functions": [],
                       "features": [],
                       "states": [
                           { "key": "state1", "stateType": "Intermediate", "transitions": [] },
                           { "key": "state2", "stateType": "Intermediate", "transitions": [{{scheduledTransitionJson ?? ""}}] }
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
