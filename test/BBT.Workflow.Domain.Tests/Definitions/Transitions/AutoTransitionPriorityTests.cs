using System.Linq;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Domain.Tests.Definitions.Transitions;

/// <summary>
/// Pins the evaluation order of <see cref="State.AutoTransitions"/> (vnext#1133): ascending
/// <see cref="Transition.EffectivePriority"/>, declaration order on ties, and the
/// DefaultAutoTransition fallback always last.
/// </summary>
public class AutoTransitionPriorityTests : DomainTestBase<DomainEntryPoint>
{
    [Fact]
    public void AutoTransitions_WithoutPriority_KeepsDeclarationOrder()
    {
        var state = StateWith(Auto("a"), Auto("b"), Auto("c"));

        state.AutoTransitions.Select(t => t.Key).ToArray().ShouldBe(["a", "b", "c"]);
    }

    [Fact]
    public void AutoTransitions_LowerPriorityComesFirst()
    {
        var state = StateWith(Auto("a", 30), Auto("b", 10), Auto("c", 20));

        state.AutoTransitions.Select(t => t.Key).ToArray().ShouldBe(["b", "c", "a"]);
    }

    [Fact]
    public void AutoTransitions_EqualPriority_KeepsDeclarationOrder()
    {
        var state = StateWith(Auto("a", 5), Auto("b", 5));

        state.AutoTransitions.Select(t => t.Key).ToArray().ShouldBe(["a", "b"]);
    }

    [Fact]
    public void AutoTransitions_Unprioritized_IsTreatedAs100()
    {
        var state = StateWith(Auto("a", 150), Auto("b"), Auto("c", 50));

        state.AutoTransitions.Select(t => t.Key).ToArray().ShouldBe(["c", "b", "a"]);
    }

    [Fact]
    public void AutoTransitions_DefaultAutoTransition_IsAlwaysLast()
    {
        var state = StateWith(Auto("d", triggerKind: 10), Auto("a"));

        state.AutoTransitions.Select(t => t.Key).ToArray().ShouldBe(["a", "d"]);
    }

    [Fact]
    public void AutoTransitions_ExcludesNonAutomatic()
    {
        var state = StateWith(Auto("a"), Manual("m"), Auto("b"));

        state.AutoTransitions.Select(t => t.Key).ToArray().ShouldBe(["a", "b"]);
    }

    [Fact]
    public void Priority_IsReadFromJson()
    {
        var state = StateWith(Auto("a", 7), Auto("b"));

        var withPriority = state.Transitions.Single(t => t.Key == "a");
        withPriority.Priority.ShouldBe(7);
        withPriority.EffectivePriority.ShouldBe(7);

        var without = state.Transitions.Single(t => t.Key == "b");
        without.Priority.ShouldBeNull();
        without.EffectivePriority.ShouldBe(Transition.DefaultPriority);
        Transition.DefaultPriority.ShouldBe(100);
    }

    private static string Auto(string key, int? priority = null, int? triggerKind = null) =>
        TransitionJson(key, "automatic", priority, triggerKind);

    private static string Manual(string key) => TransitionJson(key, "manual", null, null);

    private static string TransitionJson(string key, string triggerType, int? priority, int? triggerKind)
    {
        var extra = (priority is null ? "" : $", \"priority\": {priority}")
                    + (triggerKind is null ? "" : $", \"triggerKind\": {triggerKind}");
        return $$"""
            { "key": "{{key}}", "target": "done", "triggerType": "{{triggerType}}"{{extra}},
              "labels": [{"label": "{{key}}", "language": "en"}] }
            """;
    }

    private static State StateWith(params string[] transitionJson)
    {
        var json = $$"""
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "states": [
                {
                    "key": "initial",
                    "stateType": "initial",
                    "labels": [{"label": "Initial", "language": "en"}],
                    "transitions": [ {{string.Join(",", transitionJson)}} ]
                },
                {
                    "key": "done",
                    "stateType": "finish",
                    "labels": [{"label": "Done", "language": "en"}],
                    "transitions": []
                }
            ],
            "sharedTransitions": [],
            "startTransition": {
                "key": "start",
                "target": "initial",
                "triggerType": "manual",
                "labels": [{"label": "Start", "language": "en"}]
            }
        }
        """;

        var workflow = System.Text.Json.JsonSerializer.Deserialize<WorkflowDefinition>(
            json, JsonSerializerConstants.JsonOptions)!;
        return workflow.States.First();
    }
}
