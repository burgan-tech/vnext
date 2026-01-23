using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// Unit tests for ScheduleTransitionsStep
/// Tests timer job scheduling and cancellation for self-transitions
/// </summary>
public class ScheduleTransitionsStepTests
{
    private readonly IBackgroundJobService _mockBackgroundJobService;
    private readonly IJobScheduler _mockJobScheduler;
    private readonly ITaskTimerService _mockTaskTimerService;
    private readonly IScriptContextFactory _mockScriptContextFactory;
    private readonly IInstanceJobRepository _mockJobRepository;
    private readonly IInstanceRepository _mockInstanceRepository;
    private readonly ILogger<ScheduleTransitionsStep> _mockLogger;
    private readonly IRuntimeInfoProvider _mockRuntimeInfoProvider;
    private readonly ScheduleTransitionsStep _step;

    public ScheduleTransitionsStepTests()
    {
        _mockBackgroundJobService = Substitute.For<IBackgroundJobService>();
        _mockJobScheduler = Substitute.For<IJobScheduler>();
        _mockTaskTimerService = Substitute.For<ITaskTimerService>();
        _mockScriptContextFactory = Substitute.For<IScriptContextFactory>();
        _mockJobRepository = Substitute.For<IInstanceJobRepository>();
        _mockInstanceRepository = Substitute.For<IInstanceRepository>();
        _mockLogger = Substitute.For<ILogger<ScheduleTransitionsStep>>();
        _mockRuntimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();

        _step = new ScheduleTransitionsStep(
            _mockBackgroundJobService,
            _mockJobScheduler,
            _mockTaskTimerService,
            _mockScriptContextFactory,
            _mockJobRepository,
            _mockInstanceRepository,
            _mockLogger,
            _mockRuntimeInfoProvider);
    }

    [Fact]
    public void Order_ShouldBeSchedule()
    {
        // Assert
        _step.Order.ShouldBe(LifecycleOrder.Schedule);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoScheduledTransitions_ShouldSkip()
    {
        // Arrange
        var context = CreateTransitionExecutionContext(hasScheduledTransitions: false);

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value!.StopPipeline.ShouldBeFalse();
        await _mockJobRepository.DidNotReceive().GetListActiveAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenSelfTransition_ShouldCancelExistingTimerJobs()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var scheduledTransitionKey = "timeout";
        var existingJobId = Guid.NewGuid();
        var existingJobName = $"trans-{instanceId}-{scheduledTransitionKey}";

        var context = CreateTransitionExecutionContext(
            instanceId: instanceId,
            hasScheduledTransitions: true,
            scheduledTransitionKey: scheduledTransitionKey);

        var existingJob = InstanceJob.Create(
            existingJobId,
            existingJobName,
            existingJobId,
            context.Domain,
            context.WorkflowKey,
            instanceId);

        _mockJobRepository.GetListActiveAsync(instanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<InstanceJob> { existingJob }));

        SetupMocksForScheduling(context, scheduledTransitionKey);

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        
        // Verify existing job was canceled via IJobScheduler
        await _mockJobScheduler.Received(1).DeleteAsync(
            Arg.Any<string>(), 
            existingJobName, 
            Arg.Any<CancellationToken>());
        await _mockJobRepository.Received(1).UpdateAsync(
            Arg.Is<InstanceJob>(j => j.JobName == existingJobName && !j.IsActive),
            true,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoExistingJobs_ShouldNotAttemptCancellation()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var context = CreateTransitionExecutionContext(
            instanceId: instanceId,
            hasScheduledTransitions: true,
            scheduledTransitionKey: "timeout");

        _mockJobRepository.GetListActiveAsync(instanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<InstanceJob>()));

        SetupMocksForScheduling(context, "timeout");

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        await _mockJobScheduler.DidNotReceive().DeleteAsync(
            Arg.Any<string>(), 
            Arg.Any<string>(), 
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenExistingJobForDifferentTransition_ShouldNotCancelIt()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var existingJobId = Guid.NewGuid();
        var existingJobName = $"trans-{instanceId}-other-transition";

        var context = CreateTransitionExecutionContext(
            instanceId: instanceId,
            hasScheduledTransitions: true,
            scheduledTransitionKey: "timeout");

        var existingJob = InstanceJob.Create(
            existingJobId,
            existingJobName,
            existingJobId,
            context.Domain,
            context.WorkflowKey,
            instanceId);

        _mockJobRepository.GetListActiveAsync(instanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<InstanceJob> { existingJob }));

        SetupMocksForScheduling(context, "timeout");

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        
        // The existing job for "other-transition" should NOT be canceled
        await _mockJobScheduler.DidNotReceive().DeleteAsync(
            Arg.Any<string>(), 
            existingJobName, 
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldScheduleNewTimerJob()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var newJobId = Guid.NewGuid();
        var scheduledTransitionKey = "timeout";

        var context = CreateTransitionExecutionContext(
            instanceId: instanceId,
            hasScheduledTransitions: true,
            scheduledTransitionKey: scheduledTransitionKey);

        _mockJobRepository.GetListActiveAsync(instanceId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<InstanceJob>()));

        SetupMocksForScheduling(context, scheduledTransitionKey);
        
        _mockBackgroundJobService.EnqueueAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<object>(),
            Arg.Any<string>(),
            Arg.Any<Dictionary<string, object>>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(newJobId));

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        
        // Verify new job was scheduled
        await _mockBackgroundJobService.Received(1).EnqueueAsync(
            Arg.Any<string>(),
            Arg.Is<string>(name => name == $"trans-{instanceId}-{scheduledTransitionKey}"),
            Arg.Any<object>(),
            Arg.Any<string>(),
            Arg.Any<Dictionary<string, object>>(),
            Arg.Any<CancellationToken>());
        
        // Verify job record was persisted
        await _mockJobRepository.Received(1).InsertAsync(
            Arg.Is<InstanceJob>(j => j.InstanceId == instanceId),
            true,
            Arg.Any<CancellationToken>());
    }

    #region Helper Methods

    private void SetupMocksForScheduling(TransitionExecutionContext context, string scheduledTransitionKey)
    {
        var mockScriptContextLogger = Substitute.For<ILogger<ScriptContext>>();
        var mockScriptContext = new ScriptContext(mockScriptContextLogger);
        
        var mockBuilder = Substitute.For<IScriptContextBuilder>();
        mockBuilder.WithWorkflow(Arg.Any<Definitions.Workflow>()).Returns(mockBuilder);
        mockBuilder.WithInstance(Arg.Any<Instance>()).Returns(mockBuilder);
        mockBuilder.WithRuntime(Arg.Any<IRuntimeInfoProvider>()).Returns(mockBuilder);
        mockBuilder.WithTransition(Arg.Any<Transition>()).Returns(mockBuilder);
        mockBuilder.WithHeaders(Arg.Any<Dictionary<string, string?>>()).Returns(mockBuilder);
        mockBuilder.WithRouteValues(Arg.Any<Dictionary<string, string?>>()).Returns(mockBuilder);
        mockBuilder.BuildAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockScriptContext));

        _mockScriptContextFactory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(mockBuilder);

        var timerSchedule = TimerSchedule.FromDuration(TimeSpan.FromMinutes(5));
        _mockTaskTimerService.ExecuteTimerAsync(
            Arg.Any<ScriptCode>(),
            Arg.Any<ScriptContext>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<TimerSchedule>.Ok(timerSchedule)));
    }

    private TransitionExecutionContext CreateTransitionExecutionContext(
        Guid? instanceId = null,
        bool hasScheduledTransitions = false,
        string scheduledTransitionKey = "timeout")
    {
        instanceId ??= Guid.NewGuid();
        var workflowKey = "test-workflow";
        var domain = "test-domain";

        var workflow = CreateMockWorkflow(workflowKey, domain);
        var instance = Instance.Create(instanceId.Value, workflowKey);
        
        State state;
        if (hasScheduledTransitions)
        {
            state = CreateStateWithScheduledTransition(scheduledTransitionKey);
        }
        else
        {
            state = State.Create("state1", StateType.Intermediate, StateSubType.None, "Patch");
        }

        var transition = Transition.Create("test-transition", null, state.Key, TriggerType.Manual, "Patch");

        return new TransitionExecutionContext
        {
            InstanceId = instanceId.Value,
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = "test-transition",
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Target = state,
            Transition = transition,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private State CreateStateWithScheduledTransition(string transitionKey)
    {
        var json = $$"""
        {
            "key": "waiting",
            "stateType": "Intermediate",
            "subType": "None",
            "versionStrategy": "Patch",
            "labels": [],
            "onEntries": [],
            "onExits": [],
            "transitions": [
                {
                    "key": "{{transitionKey}}",
                    "from": "waiting",
                    "target": "waiting",
                    "triggerType": "Scheduled",
                    "versionStrategy": "Patch",
                    "timer": { "location": "inline", "code": "return TimeSpan.FromMinutes(5);" },
                    "labels": [],
                    "onExecutionTasks": []
                }
            ]
        }
        """;

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        return System.Text.Json.JsonSerializer.Deserialize<State>(json, options)!;
    }

    private Definitions.Workflow CreateMockWorkflow(string key, string domain)
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

        workflow.SetReference(new Reference(key, domain, "sys-flows", "1.0.0"));
        return workflow;
    }

    #endregion
}
