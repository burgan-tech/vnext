using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
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
/// Pins that <see cref="RunAutomaticTransitionsStep"/> evaluates the state's automatic transitions
/// in <c>State.AutoTransitions</c> order (priority first) and does not re-sort them. The fixture
/// mixes an absent and an explicit <c>triggerKind</c> on purpose: a step-local sort on
/// <c>TriggerKind</c> would put the null one first and undo the priority order.
/// </summary>
public class RunAutomaticTransitionsStepPriorityTests
{
    private readonly IAutoConditionEvaluator _evaluator = Substitute.For<IAutoConditionEvaluator>();
    private readonly RunAutomaticTransitionsStep _step;

    public RunAutomaticTransitionsStepPriorityTests()
    {
        _step = new RunAutomaticTransitionsStep(
            _evaluator,
            Substitute.For<ILogger<RunAutomaticTransitionsStep>>());
    }

    [Fact]
    public async Task ExecuteAsync_BothRulesTrue_HigherPriorityWins()
    {
        var context = CreateContext();
        _evaluator.EvaluateAsync(default!, default!, default)
            .ReturnsForAnyArgs(call => Evaluation(call.Arg<Transition>().Key, AutoConditionStatus.Satisfied));

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        context.Directives.NextTransition!.TransitionKey.ShouldBe("high");
        await _evaluator.Received(1).EvaluateAsync(
            Arg.Is<Transition>(t => t.Key == "high"), Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>());
        await _evaluator.DidNotReceive().EvaluateAsync(
            Arg.Is<Transition>(t => t.Key == "low"), Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_HigherPriorityRuleFalse_FallsThroughToNext()
    {
        var context = CreateContext();
        _evaluator.EvaluateAsync(default!, default!, default)
            .ReturnsForAnyArgs(call => Evaluation(
                call.Arg<Transition>().Key,
                call.Arg<Transition>().Key == "high" ? AutoConditionStatus.NotSatisfied : AutoConditionStatus.Satisfied));

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        context.Directives.NextTransition!.TransitionKey.ShouldBe("low");
    }

    [Fact]
    public async Task ExecuteAsync_HigherPriorityRuleFails_StopsAndFails()
    {
        var context = CreateContext();
        _evaluator.EvaluateAsync(default!, default!, default)
            .ReturnsForAnyArgs(call => call.Arg<Transition>().Key == "high"
                ? Result<AutoConditionEvaluation>.Fail(new Error("test.rule-failed", "rule failed"))
                : Evaluation("low", AutoConditionStatus.Satisfied));

        var result = await _step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        await _evaluator.DidNotReceive().EvaluateAsync(
            Arg.Is<Transition>(t => t.Key == "low"), Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>());
    }

    private static Result<AutoConditionEvaluation> Evaluation(string key, AutoConditionStatus status) =>
        Result<AutoConditionEvaluation>.Ok(new AutoConditionEvaluation { TransitionKey = key, Status = status });

    private static TransitionExecutionContext CreateContext()
    {
        var instanceId = Guid.NewGuid();
        const string workflowKey = "test-workflow";
        const string domain = "test-domain";

        var workflow = CreateWorkflow(workflowKey, domain);
        var instance = Instance.Create(instanceId, workflowKey, "1.0.0");
        var state = workflow.GetState("state1").Value!;
        var transition = Transition.Create("regular-transition", null, "state1", TriggerType.Manual, "Patch");

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = "regular-transition",
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
            Data = null,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static Definitions.Workflow CreateWorkflow(string key, string domain)
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
                        {"key": "low", "from": "state1", "target": "state2", "triggerType": "Automatic", "priority": 20, "versionStrategy": "Patch", "rule": { "code": "Y29kZQ==", "encoding": "Base64" }},
                        {"key": "high", "from": "state1", "target": "state2", "triggerType": "Automatic", "triggerKind": 0, "priority": 10, "versionStrategy": "Patch", "rule": { "code": "Y29kZQ==", "encoding": "Base64" }}
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
