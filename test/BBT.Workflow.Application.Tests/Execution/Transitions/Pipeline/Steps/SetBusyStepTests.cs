using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// Unit tests for SetBusyStep
/// Tests that instance is set to Busy status at the start of transition processing
/// </summary>
public class SetBusyStepTests
{
    private readonly IInstanceRepository _mockInstanceRepository;
    private readonly ILogger<SetBusyStep> _mockLogger;
    private readonly SetBusyStep _step;

    public SetBusyStepTests()
    {
        _mockInstanceRepository = Substitute.For<IInstanceRepository>();
        _mockLogger = Substitute.For<ILogger<SetBusyStep>>();
        _step = new SetBusyStep(_mockInstanceRepository, _mockLogger);
    }

    [Fact]
    public void Order_ShouldBeSetBusy()
    {
        // Assert
        _step.Order.ShouldBe(LifecycleOrder.SetBusy);
    }

    [Fact]
    public async Task ExecuteAsync_WhenInstanceIsActive_ShouldSetToBusy()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        context.Instance.IsActive.ShouldBeTrue();

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Instance.IsBusy.ShouldBeTrue();
        await _mockInstanceRepository.Received(1).UpdateAsync(context.Instance, true, CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_WhenInstanceIsAlreadyBusy_ShouldSkipAndNotUpdate()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        context.Instance.Busy(); // Set to Busy before test

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Instance.IsBusy.ShouldBeTrue();
        await _mockInstanceRepository.DidNotReceive().UpdateAsync(Arg.Any<Instance>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenInstanceIsCompleted_ShouldSkipAndNotUpdate()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        context.Instance.Complete(); // Set to Completed before test

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Instance.IsCompleted.ShouldBeTrue();
        await _mockInstanceRepository.DidNotReceive().UpdateAsync(Arg.Any<Instance>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenIsSubFlowResume_ShouldSkipAndNotUpdate()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();
        context.Directives.MarkAsSubFlowResume();

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Instance.IsActive.ShouldBeTrue(); // Should remain Active
        await _mockInstanceRepository.DidNotReceive().UpdateAsync(Arg.Any<Instance>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnContinueOutcome()
    {
        // Arrange
        var context = CreateTransitionExecutionContext();

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value!.StopPipeline.ShouldBeFalse();
    }

    private TransitionExecutionContext CreateTransitionExecutionContext()
    {
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var domain = "test-domain";

        var workflow = CreateMockWorkflow(workflowKey, domain);
        var instance = Instance.Create(instanceId, workflowKey);
        var state = workflow.GetState("state1").Value!;
        var transition = Transition.Create("test-transition", null, "state1", TriggerType.Manual, "Patch");

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
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
            Transition = transition,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
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
}
