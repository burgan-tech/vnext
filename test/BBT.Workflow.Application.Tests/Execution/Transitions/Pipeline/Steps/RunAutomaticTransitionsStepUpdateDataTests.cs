using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// Unit tests pinning that <see cref="RunAutomaticTransitionsStep"/> treats updateData like any
/// other transition: auto transitions are evaluated against the freshly written data regardless
/// of status ownership (updateData never owns the status — ownership for a satisfied transition
/// is acquired later, at the pipeline's continuation boundary).
/// </summary>
public class RunAutomaticTransitionsStepUpdateDataTests
{
    private readonly IAutoConditionEvaluator _evaluator = Substitute.For<IAutoConditionEvaluator>();
    private readonly RunAutomaticTransitionsStep _step;

    public RunAutomaticTransitionsStepUpdateDataTests()
    {
        _step = new RunAutomaticTransitionsStep(
            _evaluator,
            Substitute.For<ILogger<RunAutomaticTransitionsStep>>());
    }

    [Fact]
    public async Task ExecuteAsync_UpdateDataWithoutOwnership_StillEvaluatesAutoTransitions()
    {
        // The old data-only guard is gone: updateData always evaluates the state's autos.
        var context = CreateContext("update-parent-data");
        context.OwnsStatus = false;
        _evaluator.EvaluateAsync(default!, default!, default)
            .ReturnsForAnyArgs(BBT.Aether.Results.Result<AutoConditionEvaluation>.Ok(
                new AutoConditionEvaluation { TransitionKey = "auto-next", Status = AutoConditionStatus.NotSatisfied }));

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _evaluator.ReceivedWithAnyArgs()
            .EvaluateAsync(default!, default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_UpdateDataWithoutOwnership_SatisfiedCondition_RequestsNextTransition()
    {
        // A satisfied condition is requested as usual; the pipeline's continuation boundary is
        // what turns it into an owned execution (or drops it when the instance is taken).
        var context = CreateContext("update-parent-data");
        context.OwnsStatus = false;
        _evaluator.EvaluateAsync(default!, default!, default)
            .ReturnsForAnyArgs(BBT.Aether.Results.Result<AutoConditionEvaluation>.Ok(
                new AutoConditionEvaluation { TransitionKey = "auto-next", Status = AutoConditionStatus.Satisfied }));

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        context.Directives.NextTransition!.TransitionKey.ShouldBe("auto-next");
    }

    [Fact]
    public async Task ExecuteAsync_NonUpdateDataWithoutOwnership_StillEvaluates()
    {
        // The guard is updateData-specific — other transitions keep today's behavior.
        var context = CreateContext("regular-transition");
        context.OwnsStatus = false;
        _evaluator.EvaluateAsync(default!, default!, default)
            .ReturnsForAnyArgs(BBT.Aether.Results.Result<AutoConditionEvaluation>.Ok(
                new AutoConditionEvaluation { TransitionKey = "auto-next", Status = AutoConditionStatus.NotSatisfied }));

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _evaluator.ReceivedWithAnyArgs()
            .EvaluateAsync(default!, default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_SatisfiedCondition_EmitsTheAutoSelectedEventOnTheAmbientSpan()
    {
        // The selection is the transition's own work: the selected hop runs elsewhere (flat-lane
        // sibling, linked), so this event is the only in-span record of "this transition chose X".
        using var ambient = new Activity("transition/regular").Start();
        var context = CreateContext("regular-transition");
        _evaluator.EvaluateAsync(default!, default!, default)
            .ReturnsForAnyArgs(BBT.Aether.Results.Result<AutoConditionEvaluation>.Ok(
                new AutoConditionEvaluation { TransitionKey = "auto-next", Status = AutoConditionStatus.Satisfied }));

        await _step.ExecuteAsync(context, CancellationToken.None);

        var selected = ambient.Events.ShouldHaveSingleItem();
        selected.Name.ShouldBe("transition.auto.selected");
        selected.Tags.ShouldContain(tag =>
            tag.Key == "vnext.next.transition" && Equals(tag.Value, "auto-next"));
        selected.Tags.ShouldContain(tag => tag.Key == "evaluated.count");
        Activity.Current = null;
    }

    [Fact]
    public async Task ExecuteAsync_NoSatisfiedCondition_EmitsNoEvent()
    {
        using var ambient = new Activity("transition/regular").Start();
        var context = CreateContext("regular-transition");
        _evaluator.EvaluateAsync(default!, default!, default)
            .ReturnsForAnyArgs(BBT.Aether.Results.Result<AutoConditionEvaluation>.Ok(
                new AutoConditionEvaluation { TransitionKey = "auto-next", Status = AutoConditionStatus.NotSatisfied }));

        await _step.ExecuteAsync(context, CancellationToken.None);

        ambient.Events.ShouldBeEmpty();
        Activity.Current = null;
    }

    private static TransitionExecutionContext CreateContext(string transitionKey)
    {
        var instanceId = Guid.NewGuid();
        const string workflowKey = "test-workflow";
        const string domain = "test-domain";

        var workflow = CreateWorkflowWithAutoTransition(workflowKey, domain);
        var instance = Instance.Create(instanceId, workflowKey, "1.0.0");
        var state = workflow.GetState("state1").Value!;
        var transition = Transition.Create(transitionKey, null, "state1", TriggerType.Manual, "Patch");

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = transitionKey,
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Target = state, // auto evaluation reads Target
            Transition = transition,
            Instance = instance,
            Data = null,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static Definitions.Workflow CreateWorkflowWithAutoTransition(string key, string domain)
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
                    "type": "P",
                    "transitions": [
                        {"key": "auto-next", "from": "state1", "target": "state2", "triggerType": "Automatic", "versionStrategy": "Patch", "rule": { "code": "Y29kZQ==", "encoding": "Base64" }}
                    ]
                },
                {
                    "key": "state2",
                    "type": "P",
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
