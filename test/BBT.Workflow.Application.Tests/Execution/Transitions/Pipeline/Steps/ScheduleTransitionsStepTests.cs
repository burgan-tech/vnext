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
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// Unit tests for ScheduleTransitionsStep.
/// </summary>
public class ScheduleTransitionsStepTests
{
    private readonly IBackgroundJobService _backgroundJobService;
    private readonly ITaskTimerService _taskTimerService;
    private readonly IScriptContextFactory _scriptContextFactory;
    private readonly IInstanceJobRepository _jobRepository;
    private readonly IInstanceRepository _instanceRepository;
    private readonly ILogger<ScheduleTransitionsStep> _logger;
    private readonly IRuntimeInfoProvider _runtimeInfoProvider;
    private readonly ScheduleTransitionsStep _step;

    public ScheduleTransitionsStepTests()
    {
        _backgroundJobService = Substitute.For<IBackgroundJobService>();
        _taskTimerService = Substitute.For<ITaskTimerService>();
        _scriptContextFactory = Substitute.For<IScriptContextFactory>();
        _jobRepository = Substitute.For<IInstanceJobRepository>();
        _instanceRepository = Substitute.For<IInstanceRepository>();
        _logger = Substitute.For<ILogger<ScheduleTransitionsStep>>();
        _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();

        _step = new ScheduleTransitionsStep(
            _backgroundJobService,
            _taskTimerService,
            _scriptContextFactory,
            _jobRepository,
            _instanceRepository,
            _logger,
            _runtimeInfoProvider);
    }

    [Fact]
    public void Order_ShouldBeSchedule()
    {
        _step.Order.ShouldBe(LifecycleOrder.Schedule);
    }

    /// <summary>
    /// Regression test for the SubFlow + schedule-transition bug:
    ///
    /// When the parent flow resumes after subflow completion, ClearBusyOnResumeStep (order 79)
    /// sets context.Target to the parent's current SubFlow state. Without the IsSubFlowResume
    /// guard, ScheduleTransitionsStep (order 80) would then try to create Dapr scheduled jobs
    /// for the SubFlow state — which is wrong because:
    ///   1. We are about to *leave* that state via auto-transition; creating jobs now is too late.
    ///   2. If Dapr job creation fails, the parent pipeline is faulted and the overall
    ///      flow "cannot complete" even if the subflow finished successfully.
    ///   3. If job creation succeeds but the timer fires before CancelScheduledJobsStep
    ///      cancels it, the parent is incorrectly routed to the timeout/scheduled state.
    ///
    /// The guard must prevent any job creation during SubFlow resume regardless of whether
    /// the scheduled-transition state was actually entered in the child flow.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WhenIsSubFlowResume_ShouldSkipWithoutCreatingAnyJobs()
    {
        // Arrange — SubFlow state in parent has a scheduled (timeout) transition
        var subFlowState = State.Create("subflow-state", StateType.SubFlow, StateSubType.None, "Patch");
        var scheduledTransition = Transition.Create("timeout", "subflow-state", "timeout-state", TriggerType.Scheduled, "Patch");
        subFlowState.AddTransition(scheduledTransition);

        var context = CreateContext(subFlowState);
        context.Directives.MarkAsSubFlowResume(); // simulates parent resume after subflow completion

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert — step must skip; no Dapr job or InstanceJob must be created
        result.IsSuccess.ShouldBeTrue();
        result.Value!.StopPipeline.ShouldBeFalse();

        await _backgroundJobService.DidNotReceiveWithAnyArgs()
            .EnqueueAsync(default!, default!, default!, default!, default, default);
        await _jobRepository.DidNotReceiveWithAnyArgs()
            .InsertAsync(default!, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTargetStateHasNoScheduledTransitions_ShouldSkipWithoutCallingServices()
    {
        // Arrange — state with only manual transitions
        var state = State.Create("state1", StateType.Intermediate, StateSubType.None, "Patch");
        var manualTransition = Transition.Create("submit", "state1", "state2", TriggerType.Manual, "Patch");
        state.AddTransition(manualTransition);

        var context = CreateContext(state);

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        await _backgroundJobService.DidNotReceiveWithAnyArgs()
            .EnqueueAsync(default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTargetStateHasNoTransitions_ShouldSkipWithoutCallingServices()
    {
        // Arrange — state with no transitions
        var state = State.Create("state1", StateType.Intermediate, StateSubType.None, "Patch");
        var context = CreateContext(state);

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        await _backgroundJobService.DidNotReceiveWithAnyArgs()
            .EnqueueAsync(default!, default!, default!, default!, default, default);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private TransitionExecutionContext CreateContext(State targetState)
    {
        var workflow = CreateMinimalWorkflow();
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0");

        return new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = "test-domain",
            WorkflowKey = "test-flow",
            TransitionKey = "",
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.System,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = targetState,
            Target = targetState,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static Definitions.Workflow CreateMinimalWorkflow()
    {
        var json = """
                   {
                       "type": "F",
                       "timeout": null,
                       "labels": [],
                       "functions": [],
                       "features": [],
                       "states": [
                           {
                               "key": "state1",
                               "stateType": "Intermediate",
                               "transitions": []
                           }
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
        workflow.SetReference(new Reference("test-flow", "test-domain", "sys-flows", "1.0.0"));
        return workflow;
    }
}
