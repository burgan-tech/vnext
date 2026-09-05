using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Continuations;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Shared;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Continuations;

public class InlineContinuationStrategyTests
{
    [Fact]
    public async Task DispatchAsync_ShouldPreserveTerminationCascadeIdentity()
    {
        var termination = new TerminationContext(
            TerminationOrigin.ParentCascade,
            Guid.NewGuid(),
            Guid.NewGuid());
        var context = CreateContext(termination);
        context.EnqueueContinuations = true;
        context.Directives.RequestNextTransition(new NextTransitionRequest("approve"));
        var sut = new InlineContinuationStrategy();

        var result = await sut.DispatchAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        result.Value!.Termination.ShouldNotBeNull();
        result.Value.Termination!.CascadeId.ShouldBe(termination.CascadeId);
        result.Value.Termination.InitiatorInstanceId.ShouldBe(termination.InitiatorInstanceId);
        result.Value.Mode.ShouldBe(ExecMode.Sync);
        result.Value.EnqueueContinuations.ShouldBeFalse();
    }

    private static TransitionExecutionContext CreateContext(TerminationContext termination)
    {
        const string workflowKey = "test-workflow";
        const string domain = "test-domain";
        var workflow = CreateWorkflow(workflowKey, domain);
        var instance = Instance.Create(Guid.NewGuid(), workflowKey, workflow.Version);
        var state = workflow.GetState("state1").Value!;

        return new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = "test-transition",
            Trigger = TriggerType.Automatic,
            Actor = ExecutionActor.System,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16],
            Termination = termination
        };
    }

    private static Definitions.Workflow CreateWorkflow(string key, string domain)
    {
        const string json = """
        {
            "type": "F",
            "timeout": null,
            "labels": [],
            "functions": [],
            "features": [],
            "states": [
                {
                    "key": "state1",
                    "type": "P",
                    "transitions": []
                }
            ],
            "sharedTransitions": [],
            "extensions": [],
            "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
        }
        """;

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference(key, domain, "sys-flows", "1.0.0"));
        return workflow;
    }
}
