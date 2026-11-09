using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Agents;
using BBT.Workflow.Definitions;
using BBT.Workflow.Domain.Tests.Definitions.Agents;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Agents;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Agents;

/// <summary>
/// The agent's in-hop decision drives the automatic-transition evaluation of an AiAgent state (issue #1105):
/// no decision in the hop (updateData, resume) satisfies nothing the agent decides; a model decision
/// satisfies only the selected candidate; a rules-mode run leaves the candidates to their rules and keeps
/// the fallback for last.
/// </summary>
public class AgentDecisionTests
{
    private readonly ITaskConditionService _conditions = Substitute.For<ITaskConditionService>();

    private AutoConditionEvaluator Evaluator() => new(
        _conditions,
        Substitute.For<IScriptContextFactory>(),
        Substitute.For<IInstanceRepository>(),
        NullLogger<AutoConditionEvaluator>.Instance,
        Substitute.For<IRuntimeInfoProvider>());

    private static (TransitionExecutionContext Context, State State) Context(AgentHopDecision? decision)
    {
        var workflow = AiAgentStateDefinitionTests.Build();
        var state = workflow.States.Single(s => s.Key == "classifying");
        var context = new TransitionExecutionContext { Workflow = workflow, Target = state, AgentDecision = decision };
        return (context, state);
    }

    private static AgentHopDecision Decision(string? selected, AgentDecisionSource source, AgentDecisionMode mode = AgentDecisionMode.Model) =>
        new(mode, selected, source,
            new HashSet<string> { "is-question", "is-complaint", "is-fraud" },
            new HashSet<string> { "classify-failed" },
            "classify-failed",
            null);

    [Theory]
    [InlineData("is-question")]
    [InlineData("classify-failed")]
    public async Task Without_an_in_hop_decision_nothing_the_agent_decides_is_satisfied(string key)
    {
        var (context, state) = Context(null);

        var result = await Evaluator().EvaluateAsync(state.FindTransition(key)!, context);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(AutoConditionStatus.NotSatisfied);
    }

    [Fact]
    public async Task Model_decision_satisfies_only_the_selected_candidate()
    {
        var (context, state) = Context(Decision("is-complaint", AgentDecisionSource.Model));

        (await Evaluator().EvaluateAsync(state.FindTransition("is-complaint")!, context)).Value.Status.ShouldBe(AutoConditionStatus.Satisfied);
        (await Evaluator().EvaluateAsync(state.FindTransition("is-question")!, context)).Value.Status.ShouldBe(AutoConditionStatus.NotSatisfied);
        (await Evaluator().EvaluateAsync(state.FindTransition("classify-failed")!, context)).Value.Status.ShouldBe(AutoConditionStatus.NotSatisfied);
    }

    [Fact]
    public async Task Rules_mode_satisfies_the_fallback_which_is_evaluated_last()
    {
        var (context, state) = Context(Decision(null, AgentDecisionSource.Rules, AgentDecisionMode.Rules));

        (await Evaluator().EvaluateAsync(state.FindTransition("classify-failed")!, context)).Value.Status.ShouldBe(AutoConditionStatus.Satisfied);
        (await Evaluator().EvaluateAsync(state.FindTransition("is-question")!, context)).Value.Status.ShouldBe(AutoConditionStatus.NotSatisfied);
    }

    [Fact]
    public void Candidates_carry_their_when_text_by_source_order()
    {
        var state = AiAgentStateDefinitionTests.Build().States.Single(s => s.Key == "classifying");

        var candidates = AgentDecisionSupport.BuildCandidates(state, new Dictionary<string, string> { ["is-complaint"] = "Mapped." });

        candidates.Single(c => c.Key == "is-question").WhenSource.ShouldBe("description");
        candidates.Single(c => c.Key == "is-complaint").ShouldBe(new AgentCandidate("is-complaint", "Mapped.", "hintsMapping"));
        candidates.Single(c => c.Key == "is-fraud").ShouldBe(new AgentCandidate("is-fraud", "Fraud reported.", "hint"));
        candidates.ShouldNotContain(c => c.Key == "vip" || c.Key == "classify-failed");
    }

    [Fact]
    public void Model_mode_schema_gains_a_required_decision_enum_and_reason()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"category":{"type":"string"}},"required":["category"]}""").RootElement;
        var candidates = new[] { new AgentCandidate("a", "A", "key"), new AgentCandidate("b", "B", "key") };

        var augmented = AgentDecisionSupport.AugmentSchema(schema, AgentDecisionMode.Model, candidates);

        augmented.GetProperty("properties").GetProperty("decision").GetProperty("enum").EnumerateArray()
            .Select(e => e.GetString()).ShouldBe(["a", "b"]);
        augmented.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldBe(["category", "decision", "reason"]);
        AgentDecisionSupport.AugmentSchema(schema, AgentDecisionMode.Rules, candidates).GetRawText().ShouldBe(schema.GetRawText());
    }

    [Fact]
    public void Template_renders_data_body_instance_and_missing_paths()
    {
        var data = JsonDocument.Parse("""{"customer":{"name":"Ada","tags":["vip","new"]},"amount":12.5}""").RootElement;
        var id = Guid.NewGuid();
        var scope = new AgentTemplateRenderer.Scope(data, null, id, "k-1", "classifying");

        AgentTemplateRenderer.Render("{{ data.customer.name }}|{{data.customer.tags[1]}}|{{ data.amount }}|{{ data.missing }}|{{ instance.id }}|{{ state }}", scope)
            .ShouldBe($"Ada|new|12.5||{id}|classifying");
    }

    [Fact]
    public void Policy_is_narrowed_by_the_state_and_capped_by_the_environment()
    {
        var policy = EffectiveAgentPolicy.Resolve(
            new AgentPolicy { MaxIterations = 10, MaxToolCalls = 8, TimeoutSeconds = 300 },
            new AgentPolicy { MaxToolCalls = 3, MaxIterations = 50 },
            new AiAgentOptions { MaxRunSeconds = 120, MaxIterations = 12 });

        policy.MaxToolCalls.ShouldBe(3);
        policy.MaxIterations.ShouldBe(10);
        policy.TimeoutSeconds.ShouldBe(120);
    }
}
