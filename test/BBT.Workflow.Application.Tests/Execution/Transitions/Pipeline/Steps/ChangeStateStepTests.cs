using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// Unit tests for <see cref="ChangeStateStep"/>.
/// Focuses on the cached ScriptContext snapshot being re-based to the new state after a
/// state change, which is what allows OnEntry tasks and state-level error boundary resolution
/// to observe the target state rather than the stale (source) snapshot.
/// </summary>
public class ChangeStateStepTests
{
    private const string Domain = "test-domain";
    private const string WorkflowKey = "test-workflow";

    private readonly IInstanceRepository _mockInstanceRepository;
    private readonly IWorkflowMetrics _mockMetrics;
    private readonly ChangeStateStep _step;

    public ChangeStateStepTests()
    {
        _mockInstanceRepository = Substitute.For<IInstanceRepository>();
        _mockMetrics = Substitute.For<IWorkflowMetrics>();
        _step = new ChangeStateStep(
            _mockInstanceRepository,
            _mockMetrics,
            Substitute.For<ILogger<ChangeStateStep>>());
    }

    [Fact]
    public void Order_ShouldBeChangeState()
    {
        _step.Order.ShouldBe(LifecycleOrder.ChangeState);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStateChanges_ShouldRefreshCachedScriptContextToTargetState()
    {
        // Arrange: instance currently in state1, cached ScriptContext frozen at state1.
        var context = CreateContext(out var workflow, out var instance);
        instance.ChangeState(workflow.GetState("state1").Value!);

        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetWorkflow(workflow)
            .SetInstance(instance.CreateSnapshot()) // frozen snapshot at state1 (mirrors WithInstance)
            .SetTaskResponse(new Dictionary<string, object?> { ["onExecuteTask"] = "result" })
            .Build();

        scriptContext.Instance!.CurrentState.ShouldBe("state1");
        context.Cache["ScriptContext"] = scriptContext;

        // Act: transition state1 -> state2.
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Instance.GetCurrentState.ShouldBe("state2");

        // Same cached ScriptContext object retained, but its snapshot now reflects the target state.
        var cached = (ScriptContext)context.Cache["ScriptContext"]!;
        ReferenceEquals(cached, scriptContext).ShouldBeTrue();
        cached.Instance!.CurrentState.ShouldBe("state2");

        // Accumulated task responses are preserved across the refresh.
        cached.TaskResponse.ShouldContainKey("onExecuteTask");
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutTransition_ShouldRefreshCachedScriptContextToTargetState()
    {
        // Arrange: timeout path pre-resolves the target state and bypasses the normal flow.
        var context = CreateContext(out var workflow, out var instance);
        instance.ChangeState(workflow.GetState("state1").Value!);
        context.Directives.MarkAsTimeoutTransition();
        context.Target = workflow.GetState("state2").Value!;

        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetWorkflow(workflow)
            .SetInstance(instance.CreateSnapshot())
            .Build();
        scriptContext.Instance!.CurrentState.ShouldBe("state1");
        context.Cache["ScriptContext"] = scriptContext;

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.Instance.GetCurrentState.ShouldBe("state2");
        ((ScriptContext)context.Cache["ScriptContext"]!).Instance!.CurrentState.ShouldBe("state2");
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoScriptContextCached_ShouldStillChangeStateWithoutBuildingOne()
    {
        // Arrange
        var context = CreateContext(out var workflow, out var instance);
        instance.ChangeState(workflow.GetState("state1").Value!);

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert: state changed, and the refresh helper did not force-build a ScriptContext.
        result.IsSuccess.ShouldBeTrue();
        context.Instance.GetCurrentState.ShouldBe("state2");
        context.Cache.ContainsKey("ScriptContext").ShouldBeFalse();
    }

    private TransitionExecutionContext CreateContext(out Definitions.Workflow workflow, out Instance instance)
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
