using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Evaluators;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Evaluation;

/// <summary>
/// The automatic-transition rule reads <c>input</c> as the hop's request payload
/// (<see cref="TransitionExecutionContext.HopInput"/>) even when no task step of the hop built the shared context
/// (before, it was null unless an onExecute/onExit/onEntry task step happened to run first), and as <c>null</c> on an
/// automatic continuation hop, which carries no request payload.
/// </summary>
public sealed class AutoConditionEvaluatorHopInputTests
{
    private readonly ITaskConditionService _conditions = Substitute.For<ITaskConditionService>();
    private readonly IScriptContextFactory _factory = Substitute.For<IScriptContextFactory>();
    private readonly AutoConditionEvaluator _evaluator;

    public AutoConditionEvaluatorHopInputTests()
    {
        // The real Dynamic Expresso condition evaluator behind the (substituted) condition service.
        var expresso = new DynamicExpressoConditionEvaluator(NullLogger<DynamicExpressoConditionEvaluator>.Instance);
        _conditions.ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => expresso.EvaluateAsync(ci.Arg<ScriptCode>(), ci.Arg<ScriptContext>(), ci.Arg<CancellationToken>()));

        // The builder hands back a plain context: no transition record attached, as when no task step ran.
        var builder = Substitute.For<IScriptContextBuilder>();
        builder.WithWorkflow(Arg.Any<Definitions.Workflow?>()).Returns(builder);
        builder.WithInstance(Arg.Any<Instance?>()).Returns(builder);
        builder.WithBody(Arg.Any<object?>()).Returns(builder);
        builder.WithRuntime(Arg.Any<IRuntimeInfoProvider>()).Returns(builder);
        builder.WithHeaders(Arg.Any<Dictionary<string, string?>?>()).Returns(builder);
        builder.WithTransition(Arg.Any<Transition?>()).Returns(builder);
        builder.BuildAsync(Arg.Any<CancellationToken>())
            .Returns(ci => new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
                .SetBody(new Dictionary<string, object> { ["route"] = "from-body" })
                .Build());
        _factory.NewBuilder(Arg.Any<IInstanceRepository>()).Returns(builder);

        _evaluator = new AutoConditionEvaluator(
            _conditions,
            _factory,
            Substitute.For<IInstanceRepository>(),
            NullLogger<AutoConditionEvaluator>.Instance,
            Substitute.For<IRuntimeInfoProvider>());
    }

    [Fact]
    public async Task Rule_ReadsInput_OnAHopWithoutTaskSteps()
    {
        var context = Context(hopInput: JsonDocument.Parse("""{"route":"fast"}""").RootElement);

        var result = await _evaluator.EvaluateAsync(Rule("input.route.ToString() == \"fast\""), context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value!.Status.ShouldBe(AutoConditionStatus.Satisfied);
    }

    [Fact]
    public async Task Rule_SeesNullInput_OnAnAutomaticContinuationHop()
    {
        // The continuation's context: no request payload; Data (rebased onto instance data) never stands in for it.
        var context = Context(hopInput: null);
        context.Data = JsonDocument.Parse("""{"route":"fast"}""").RootElement;

        var result = await _evaluator.EvaluateAsync(Rule("input == null"), context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value!.Status.ShouldBe(AutoConditionStatus.Satisfied);
    }

    private static Transition Rule(string expression)
    {
        var rule = Transition.Create("to-next", "review", "next", TriggerType.Automatic, "Patch");
        rule.SetRule("dynamicExpresso", Convert.ToBase64String(Encoding.UTF8.GetBytes(expression)));
        return rule;
    }

    private static TransitionExecutionContext Context(object? hopInput) => new()
    {
        InstanceId = Guid.NewGuid(),
        Domain = "d",
        WorkflowKey = "w",
        TransitionKey = "submit",
        Trigger = TriggerType.Manual,
        Actor = ExecutionActor.System,
        CorrelationId = "c",
        ExecutionChainId = "e",
        RequestedAt = DateTimeOffset.UtcNow,
        Headers = new Dictionary<string, string?>(),
        TraceId = "t",
        SpanId = "s",
        HopInput = hopInput,
    };
}
