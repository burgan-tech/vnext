using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// Unit tests for <see cref="ClearBusyOnResumeStep"/>.
/// On the subflow-resume path the state is established here (ChangeStateStep is skipped), so the
/// cached ScriptContext snapshot must be re-based to the resumed state for downstream consumers.
/// </summary>
public class ClearBusyOnResumeStepTests
{
    private const string Domain = "test-domain";
    private const string WorkflowKey = "test-workflow";

    private readonly ClearBusyOnResumeStep _step = new();

    [Fact]
    public void Order_ShouldBeClearBusyOnResumeStep()
    {
        _step.Order.ShouldBe(LifecycleOrder.ClearBusyOnResumeStep);
    }

    [Fact]
    public async Task ExecuteAsync_OnSubFlowResume_ShouldRefreshCachedScriptContextToResumedState()
    {
        // Arrange: instance resumed in state2; cached ScriptContext still frozen at state1.
        var instanceId = Guid.NewGuid();
        var workflow = CreateWorkflow();
        var instance = Instance.Create(instanceId, WorkflowKey, "1.0.0");
        instance.ChangeState(workflow.GetState("state1").Value!);

        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetWorkflow(workflow)
            .SetInstance(instance.CreateSnapshot())
            .Build();
        scriptContext.Instance!.CurrentState.ShouldBe("state1");

        // Now the instance is actually resumed in state2.
        instance.ChangeState(workflow.GetState("state2").Value!);

        var context = new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = Domain,
            WorkflowKey = WorkflowKey,
            TransitionKey = "resume",
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.System,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = workflow.GetState("state2").Value!,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
        context.Cache["ScriptContext"] = scriptContext;
        context.Directives.MarkAsSubFlowResume();

        // Act
        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        ((ScriptContext)context.Cache["ScriptContext"]!).Instance!.CurrentState.ShouldBe("state2");
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
