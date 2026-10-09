using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Validators;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Domain.Tests.Definitions.Validators;

/// <summary>
/// The one-shot shape a <c>history: none</c> flow must have (vnext#1006). Each test mutates a valid
/// minimal flow (start → work → done) and asserts the rule's error and member path.
/// </summary>
public class WorkflowValidatorHistoryNoneTests : DomainTestBase<DomainEntryPoint>
{
    private const string Marker = "history 'none'";
    private readonly WorkflowValidator _validator = new();

    [Fact]
    public void MinimalOneShotFlow_IsValid()
    {
        HistoryNoneErrors(Build()).ShouldBeEmpty();
    }

    [Fact]
    public void FullHistoryFlow_WithManualTransition_HasNoHistoryNoneErrors()
    {
        var errors = HistoryNoneErrors(Build(root =>
        {
            root["history"] = "full";
            Transition(root, "work")["triggerType"] = "manual";
        }));
        errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("scheduled")]
    [InlineData("event")]
    public void NonAutomaticStateTransition_IsRejected(string triggerType)
    {
        var error = SingleHistoryNoneError(Build(root => Transition(root, "work")["triggerType"] = triggerType));
        error.MemberNames.ShouldContain("Workflow.States[work].Transitions[go].TriggerType");
    }

    [Fact]
    public void MissingFinishState_IsRejected()
    {
        var result = _validator.Validate(Build(root =>
        {
            var states = (JsonArray)root["states"]!;
            states.RemoveAt(1);
            Transition(root, "work")["target"] = "$self";
        }));
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("at least one Finish state") && e.MemberNames.Contains("Workflow.States"));
    }

    [Fact]
    public void NonFinishStateWithoutTransitions_IsRejected()
    {
        var result = _validator.Validate(Build(root => State(root, "work")["transitions"] = new JsonArray()));
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("has no transitions") && e.MemberNames.Contains("Workflow.States[work].Transitions"));
    }

    [Fact]
    public void SharedTransitions_AreRejected()
    {
        var result = _validator.Validate(Build(root => root["sharedTransitions"] = JsonNode.Parse("""
            [ { "key": "poke", "target": "done", "triggerType": "manual", "versionStrategy": "Minor",
                "availableIn": ["work"], "labels": [{"label": "Poke", "language": "en"}] } ]
            """)));
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("Workflow.SharedTransitions"));
    }

    [Theory]
    [InlineData("cancel", "Workflow.Cancel")]
    [InlineData("exit", "Workflow.Exit")]
    public void ClientInvokedTransitions_AreRejected(string name, string member)
    {
        var result = _validator.Validate(Build(root => root[name] = JsonNode.Parse($$"""
            { "key": "{{name}}", "target": "done", "triggerType": "manual", "versionStrategy": "Minor",
              "labels": [{"label": "X", "language": "en"}] }
            """)));
        result.ValidationErrors.ShouldContain(e => e.ErrorMessage!.Contains(Marker) && e.MemberNames.Contains(member));
    }

    [Fact]
    public void WizardState_IsRejected()
    {
        var result = _validator.Validate(Build(root => State(root, "work")["stateType"] = "wizard"));
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("Workflow.States[work].StateType"));
    }

    [Theory]
    [InlineData("suspended")]
    [InlineData("busy")]
    [InlineData("human")]
    public void ParkingSubTypes_AreRejected(string subType)
    {
        var result = _validator.Validate(Build(root => State(root, "work")["subType"] = subType));
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("Workflow.States[work].SubType"));
    }

    [Fact]
    public void LongPollInteraction_IsRejected()
    {
        var result = _validator.Validate(Build(root =>
            State(root, "work")["interaction"] = JsonNode.Parse("""{ "longPoll": { "terminate": true } }""")));
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("Workflow.States[work].Interaction.LongPoll"));
    }

    [Fact]
    public void CycleThatNeverFinishes_IsRejected()
    {
        // work ↔ loop; done exists but nothing reaches it.
        var result = _validator.Validate(Build(root =>
        {
            Transition(root, "work")["target"] = "loop";
            ((JsonArray)root["states"]!).Add(JsonNode.Parse("""
                { "key": "loop", "stateType": "intermediate", "labels": [{"label": "Loop", "language": "en"}],
                  "transitions": [ { "key": "back", "target": "work", "triggerType": "automatic", "versionStrategy": "Minor",
                                     "labels": [{"label": "Back", "language": "en"}] } ] }
                """));
        }));
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("cannot reach a Finish state") && e.MemberNames.Contains("Workflow.States[work].Transitions"));
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("cannot reach a Finish state") && e.MemberNames.Contains("Workflow.States[loop].Transitions"));
    }

    [Fact]
    public void SelfOnlyAutomaticTransition_IsRejected()
    {
        var result = _validator.Validate(Build(root => Transition(root, "work")["target"] = "$self"));
        result.ValidationErrors.ShouldContain(e => e.ErrorMessage!.Contains("cannot reach a Finish state"));
    }

    [Fact]
    public void MultipleAutomaticTransitions_AreAccepted()
    {
        HistoryNoneErrors(Build(root => ((JsonArray)State(root, "work")["transitions"]!).Add(JsonNode.Parse("""
            { "key": "other", "target": "done", "triggerType": "automatic", "versionStrategy": "Minor",
              "labels": [{"label": "Other", "language": "en"}] }
            """)))).ShouldBeEmpty();
    }

    [Fact]
    public void MultipleViolations_AreAllReportedAsErrors()
    {
        var result = _validator.Validate(Build(root =>
        {
            Transition(root, "work")["triggerType"] = "manual";
            State(root, "work")["subType"] = "human";
        }));
        result.IsValid.ShouldBeFalse();
        HistoryNoneErrors(result).Count.ShouldBeGreaterThanOrEqualTo(2);
        result.Warnings.ShouldNotContain(w => w.ErrorMessage!.Contains(Marker));
    }

    private System.Collections.Generic.List<System.ComponentModel.DataAnnotations.ValidationResult> HistoryNoneErrors(WorkflowDefinition workflow)
        => HistoryNoneErrors(_validator.Validate(workflow));

    private static System.Collections.Generic.List<System.ComponentModel.DataAnnotations.ValidationResult> HistoryNoneErrors(WorkflowValidationResult result)
        => result.ValidationErrors.Where(e => e.ErrorMessage!.Contains(Marker)).ToList();

    private System.ComponentModel.DataAnnotations.ValidationResult SingleHistoryNoneError(WorkflowDefinition workflow)
        => HistoryNoneErrors(workflow).ShouldHaveSingleItem();

    private static JsonObject State(JsonObject root, string key)
        => ((JsonArray)root["states"]!).Select(s => s!.AsObject()).Single(s => (string?)s["key"] == key);

    private static JsonObject Transition(JsonObject root, string stateKey)
        => ((JsonArray)State(root, stateKey)["transitions"]!)[0]!.AsObject();

    private static WorkflowDefinition Build(Action<JsonObject>? mutate = null)
    {
        var root = JsonNode.Parse("""
        {
            "type": "F",
            "history": "none",
            "labels": [{"label": "Test", "language": "en"}],
            "startTransition": {
                "key": "start", "target": "work", "triggerType": "manual", "versionStrategy": "Minor",
                "labels": [{"label": "Start", "language": "en"}]
            },
            "states": [
                { "key": "work", "stateType": "intermediate", "labels": [{"label": "Work", "language": "en"}],
                  "transitions": [ { "key": "go", "target": "done", "triggerType": "automatic", "versionStrategy": "Minor",
                                     "labels": [{"label": "Go", "language": "en"}] } ] },
                { "key": "done", "stateType": "finish", "labels": [{"label": "Done", "language": "en"}] }
            ],
            "sharedTransitions": []
        }
        """)!.AsObject();
        mutate?.Invoke(root);
        var workflow = JsonSerializer.Deserialize<WorkflowDefinition>(root.ToJsonString(), JsonSerializerConstants.JsonOptions)!;
        workflow.SetReference(new Reference("test-flow", "test-domain", "sys-flows", "1.0.0"));
        return workflow;
    }
}
