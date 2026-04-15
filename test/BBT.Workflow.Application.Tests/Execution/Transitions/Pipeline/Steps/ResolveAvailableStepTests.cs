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
/// Unit tests for ResolveAvailableStep
/// Tests that instance is set to Available (Active) status when appropriate conditions are met
/// </summary>
public class ResolveAvailableStepTests
{
    private readonly ILogger<ResolveAvailableStep> _mockLogger;
    private readonly ResolveAvailableStep _step;

    public ResolveAvailableStepTests()
    {
        _mockLogger = Substitute.For<ILogger<ResolveAvailableStep>>();
        _step = new ResolveAvailableStep(_mockLogger);
    }

    [Fact]
    public void Order_ShouldBeResolveAvailable()
    {
        // Assert
        _step.Order.ShouldBe(LifecycleOrder.ResolveAvailable);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAllConditionsMet_ShouldDeferActiveStatus()
    {
        // Arrange
        var context = CreateTransitionExecutionContext(hasOnlyManualTransitions: true);
        context.Instance.Busy(); // Set to Busy first

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Instance.IsBusy.ShouldBeTrue(); // Instance stays Busy — not updated directly
        context.Directives.ResolvedStatus.ShouldBe(InstanceStatus.Active); // Deferred to directives
    }

    [Fact]
    public async Task ExecuteAsync_WhenInstanceNotBusy_ShouldSkipAndNotDeferStatus()
    {
        // Arrange
        var context = CreateTransitionExecutionContext(hasOnlyManualTransitions: true);
        // Instance is Active by default

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Directives.ResolvedStatus.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenInstanceIsCompleted_ShouldSkipAndNotDeferStatus()
    {
        // Arrange
        var context = CreateTransitionExecutionContext(hasOnlyManualTransitions: true);
        context.Instance.Complete("test-domain");

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Directives.ResolvedStatus.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenNextTransitionRequested_ShouldNotDeferStatus()
    {
        // Arrange
        var context = CreateTransitionExecutionContext(hasOnlyManualTransitions: true);
        context.Instance.Busy();
        context.Directives.RequestNextTransition(new NextTransitionRequest("auto-transition", "auto"));

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Instance.IsBusy.ShouldBeTrue();
        context.Directives.ResolvedStatus.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTerminalReached_ShouldNotDeferStatus()
    {
        // Arrange
        var context = CreateTransitionExecutionContext(hasOnlyManualTransitions: true);
        context.Instance.Busy();
        context.Directives.MarkTerminal();

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Instance.IsBusy.ShouldBeTrue();
        context.Directives.ResolvedStatus.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTargetIsFinishState_ShouldNotDeferStatus()
    {
        // Arrange
        var context = CreateTransitionExecutionContextWithFinishState();
        context.Instance.Busy();

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Instance.IsBusy.ShouldBeTrue();
        context.Directives.ResolvedStatus.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTargetHasAutoTransitions_ShouldNotDeferStatus()
    {
        // Arrange
        var context = CreateTransitionExecutionContext(hasOnlyManualTransitions: false);
        context.Instance.Busy();

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Instance.IsBusy.ShouldBeTrue();
        context.Directives.ResolvedStatus.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTargetIsNull_ShouldSkipAndNotDeferStatus()
    {
        // Arrange
        var context = CreateTransitionExecutionContext(hasOnlyManualTransitions: true);
        context.Instance.Busy();
        context.Target = null;

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Instance.IsBusy.ShouldBeTrue();
        context.Directives.ResolvedStatus.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTargetHasNoTransitions_ShouldDeferActiveStatus()
    {
        // Arrange - state with no transitions should be Available
        var context = CreateTransitionExecutionContextWithNoTransitions();
        context.Instance.Busy();

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Instance.IsBusy.ShouldBeTrue(); // Instance stays Busy — not updated directly
        context.Directives.ResolvedStatus.ShouldBe(InstanceStatus.Active); // Deferred to directives
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnContinueOutcome()
    {
        // Arrange
        var context = CreateTransitionExecutionContext(hasOnlyManualTransitions: true);
        context.Instance.Busy();

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value!.StopPipeline.ShouldBeFalse();
    }

    private TransitionExecutionContext CreateTransitionExecutionContext(bool hasOnlyManualTransitions)
    {
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var domain = "test-domain";

        var workflow = CreateMockWorkflow(workflowKey, domain, hasOnlyManualTransitions);
        var instance = Instance.Create(instanceId, workflowKey, "1.0.0");
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
            Target = state,
            Transition = transition,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private TransitionExecutionContext CreateTransitionExecutionContextWithFinishState()
    {
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var domain = "test-domain";

        var workflow = CreateMockWorkflowWithFinishState(workflowKey, domain);
        var instance = Instance.Create(instanceId, workflowKey, "1.0.0");
        var finishState = workflow.GetState("finish").Value!;
        var transition = Transition.Create("complete", "state1", "finish", TriggerType.Manual, "Patch");

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = "complete",
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = workflow.GetState("state1").Value!,
            Target = finishState,
            Transition = transition,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private TransitionExecutionContext CreateTransitionExecutionContextWithNoTransitions()
    {
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var domain = "test-domain";

        var workflow = CreateMockWorkflowWithNoTransitions(workflowKey, domain);
        var instance = Instance.Create(instanceId, workflowKey, "1.0.0");
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
            Target = state,
            Transition = transition,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private Definitions.Workflow CreateMockWorkflow(string key, string domain, bool hasOnlyManualTransitions)
    {
        var transitionsJson = hasOnlyManualTransitions
            ? """[{"key": "submit", "from": "state1", "target": "state2", "triggerType": "Manual", "versionStrategy": "Patch"}]"""
            : """[{"key": "auto", "from": "state1", "target": "state2", "triggerType": "Automatic", "versionStrategy": "Patch"}]""";

        var json = $$"""
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
                    "transitions": {{transitionsJson}}
                },
                {
                    "key": "state2",
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

    private Definitions.Workflow CreateMockWorkflowWithFinishState(string key, string domain)
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
                    "transitions": [{"key": "complete", "from": "state1", "target": "finish", "triggerType": "Manual", "versionStrategy": "Patch"}]
                },
                {
                    "key": "finish",
                    "stateType": "Finish",
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

    private Definitions.Workflow CreateMockWorkflowWithNoTransitions(string key, string domain)
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
