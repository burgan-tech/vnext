using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// Unit tests for the chain-token gate in <see cref="HandleCancelPreflightStep"/>.
/// Characterizes the #725 gate behaviour the Busy-subtype chain-ownership fix relies on:
/// once a resting Busy instance has its ChainToken released, a legitimate foreign transition
/// (e.g. a child sub-process triggering the initiator's "Ready") is no longer rejected.
/// </summary>
public class HandleCancelPreflightStepTests
{
    private readonly HandleCancelPreflightStep _step;

    public HandleCancelPreflightStepTests()
    {
        var options = Options.Create(new WorkflowExecutionOptions { StrictChainTokenGate = true });
        _step = new HandleCancelPreflightStep(
            new ReservedTransitionResolver(),
            options,
            Substitute.For<ILogger<HandleCancelPreflightStep>>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenBusyWithForeignChainToken_ShouldRejectWithLockConflict()
    {
        // Busy + chain token set, incoming transition carries no matching token → foreign → rejected.
        var context = CreateContext();
        context.Instance.BeginChain(Guid.NewGuid());

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenBusyButChainTokenReleased_ShouldNotReject()
    {
        // The fix releases the ChainToken when an instance rests in a Busy-subtype state.
        // The gate keys on ChainToken presence, so a foreign transition is now accepted.
        var context = CreateContext();
        context.Instance.Busy(); // Busy, but no chain token

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.StopPipeline.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenBusyWithMatchingChainToken_ShouldNotReject()
    {
        // The chain's own continuation carries the matching token → accepted (gate preserved).
        var context = CreateContext();
        var token = Guid.NewGuid();
        context.Instance.BeginChain(token);
        context.ChainToken = token;

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.StopPipeline.ShouldBeFalse();
    }

    private static TransitionExecutionContext CreateContext()
    {
        var instanceId = Guid.NewGuid();
        const string workflowKey = "test-workflow";
        const string domain = "test-domain";

        var workflow = CreateMockWorkflow(workflowKey, domain);
        var instance = Instance.Create(instanceId, workflowKey, "1.0.0");
        var state = workflow.GetState("state1").Value!;
        var transition = Transition.Create("Ready", "state1", "state2", TriggerType.Manual, "Patch");

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = "Ready",
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Target = workflow.GetState("state2").Value!,
            Transition = transition,
            Instance = instance,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static Definitions.Workflow CreateMockWorkflow(string key, string domain)
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
                    "stateType": "Initial",
                    "transitions": [{"key": "Ready", "from": "state1", "target": "state2", "triggerType": "Manual", "versionStrategy": "Patch"}]
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
}
