using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Execution.Pipeline;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Domain.Tests.Definitions.Agents;

/// <summary>
/// AiAgent state (issue #1105): the <c>agent</c> block deserializes, rule-less automatic transitions are
/// candidates (not a "missing rule" error), reserved exits are excluded from the candidates, and the
/// validator rejects an incomplete or unsafe agent state.
/// </summary>
public class AiAgentStateDefinitionTests
{
    private readonly WorkflowValidator _validator = new();

    [Fact]
    public void Agent_block_deserializes()
    {
        var state = Build().States.Single(s => s.Key == "classifying");

        state.StateType.ShouldBe(StateType.AiAgent);
        state.Agent.ShouldNotBeNull();
        state.Agent!.Agent!.Key.ShouldBe("triage-agent");
        state.Agent.Output!.Path.ShouldBe("$.triage");
        state.Agent.Decision!.EffectiveMode.ShouldBe(AgentDecisionMode.Model);
        state.Agent.Decision.Fallback.ShouldBe("classify-failed");
        state.Agent.Decision.Hints!["is-fraud"].ShouldBe("Fraud reported.");
        state.FindTransition("is-question")!.Description.ShouldBe("The customer asks for information.");
    }

    [Fact]
    public void Valid_agent_state_passes_and_rule_less_candidates_need_no_rule()
    {
        var result = _validator.Validate(Build());

        result.ValidationErrors.Select(e => e.ErrorMessage).ShouldBeEmpty();
    }

    [Fact]
    public void Candidates_exclude_reserved_exits_rule_bearing_transitions_and_exclude_list()
    {
        var state = Build(exclude: "\"is-fraud\"", hints: "{}").States.Single(s => s.Key == "classifying");

        AgentCandidateSet.Resolve(state).Select(t => t.Key).ShouldBe(["is-question", "is-complaint"]);
    }

    [Fact]
    public void Missing_fallback_is_rejected()
    {
        var result = _validator.Validate(Build(fallback: null));

        result.ValidationErrors.ShouldContain(e => e.ErrorMessage!.Contains("must declare decision.fallback"));
    }

    [Fact]
    public void Fallback_that_is_not_an_automatic_transition_is_rejected()
    {
        var result = _validator.Validate(Build(fallback: "\"unknown\""));

        result.ValidationErrors.ShouldContain(e => e.ErrorMessage!.Contains("decision.fallback 'unknown' must be an automatic transition"));
    }

    [Fact]
    public void Model_mode_with_fewer_than_two_candidates_is_rejected()
    {
        var result = _validator.Validate(Build(exclude: "\"is-fraud\", \"is-complaint\"", hints: "{}"));

        result.ValidationErrors.ShouldContain(e => e.ErrorMessage!.Contains("needs at least two"));
    }

    [Fact]
    public void Hint_for_a_non_candidate_is_rejected()
    {
        var result = _validator.Validate(Build(hints: "{ \"classify-failed\": \"x\" }"));

        result.ValidationErrors.ShouldContain(e => e.ErrorMessage!.Contains("decision.hints key 'classify-failed' is not a candidate"));
    }

    [Fact]
    public void Default_auto_transition_other_than_the_fallback_is_rejected()
    {
        var result = _validator.Validate(Build(extraTransition:
            """, { "key": "always", "target": "done", "triggerType": 1, "triggerKind": 10, "versionStrategy": "Minor", "labels": [{"label": "L", "language": "en"}], "onExecutionTasks": [] }"""));

        result.ValidationErrors.ShouldContain(e => e.ErrorMessage!.Contains("DefaultAutoTransition 'always' would fire"));
    }

    [Fact]
    public void Agent_block_on_another_state_type_is_rejected()
    {
        var result = _validator.Validate(Build(stateType: 2));

        result.ValidationErrors.ShouldContain(e => e.ErrorMessage!.Contains("stateType is not AiAgent"));
    }

    [Fact]
    public void Rule_less_automatic_transition_outside_an_agent_state_still_needs_a_rule()
    {
        var result = _validator.Validate(Build(stateType: 2));

        result.ValidationErrors.ShouldContain(e => e.ErrorMessage!.Contains("Auto transition 'is-question' must have a rule defined"));
    }

    [Fact]
    public void Self_target_profile_excludes_the_agent_step()
    {
        PipelineExecutionProfile.ForSelfTarget(PipelineExecutionProfile.ForManual())
            .ExcludedStepOrders.ShouldContain(LifecycleOrder.AgentRun);
        PipelineExecutionProfile.ForAutoChain().ExcludedStepOrders.ShouldNotContain(LifecycleOrder.AgentRun);
    }

    public static WorkflowDefinition Build(
        string? fallback = "\"classify-failed\"",
        string exclude = "",
        string hints = "{ \"is-fraud\": \"Fraud reported.\" }",
        int stateType = 6,
        string extraTransition = "")
    {
        var fallbackJson = fallback is null ? "" : $"\"fallback\": {fallback},";
        var json = $$"""
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "startTransition": { "key": "start", "target": "start", "triggerType": 0, "versionStrategy": "Minor",
                                 "labels": [{"label": "Start", "language": "en"}], "onExecutionTasks": [] },
            "states": [
                { "key": "start", "stateType": 1, "labels": [{"label": "Start", "language": "en"}],
                  "transitions": [ { "key": "go", "target": "classifying", "triggerType": 0, "versionStrategy": "Minor",
                                     "labels": [{"label": "Go", "language": "en"}], "onExecutionTasks": [] } ] },
                { "key": "classifying", "stateType": {{stateType}}, "subType": 5, "labels": [{"label": "Classifying", "language": "en"}],
                  "agent": {
                      "agent": { "key": "triage-agent", "domain": "core", "flow": "sys-agents", "version": "1.0.0" },
                      "prompt": { "template": "Classify the message." },
                      "output": { "path": "$.triage" },
                      "decision": { "mode": "model", {{fallbackJson}} "exclude": [{{exclude}}], "hints": {{hints}} }
                  },
                  "transitions": [
                      { "key": "is-question", "target": "done", "triggerType": 1, "versionStrategy": "Minor", "labels": [{"label": "L", "language": "en"}], "onExecutionTasks": [],
                        "description": "The customer asks for information." },
                      { "key": "is-complaint", "target": "done", "triggerType": 1, "versionStrategy": "Minor", "labels": [{"label": "L", "language": "en"}], "onExecutionTasks": [] },
                      { "key": "is-fraud", "target": "done", "triggerType": 1, "versionStrategy": "Minor", "labels": [{"label": "L", "language": "en"}], "onExecutionTasks": [] },
                      { "key": "vip", "target": "done", "triggerType": 1, "versionStrategy": "Minor", "labels": [{"label": "L", "language": "en"}], "onExecutionTasks": [],
                        "rule": { "location": "dynamicExpresso", "code": "true", "encoding": "NAT" } },
                      { "key": "classify-failed", "target": "done", "triggerType": 1, "versionStrategy": "Minor", "labels": [{"label": "L", "language": "en"}], "onExecutionTasks": [] }
                      {{extraTransition}}
                  ] },
                { "key": "done", "stateType": 3, "subType": 1, "labels": [{"label": "Done", "language": "en"}] }
            ]
        }
        """;
        return JsonSerializer.Deserialize<WorkflowDefinition>(json, JsonSerializerConstants.JsonOptions)!;
    }
}
