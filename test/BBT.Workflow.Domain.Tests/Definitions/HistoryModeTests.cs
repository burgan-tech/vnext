using System;
using System.Text.Json;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Unit tests for <see cref="HistoryMode"/> (vnext#1006) and how <c>attributes.history</c> binds on the
/// workflow definition.
/// </summary>
public sealed class HistoryModeTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("NONE")]
    [InlineData(" none ")]
    public void FromCode_ResolvesNone(string code) =>
        HistoryMode.FromCode(code).ShouldBe(HistoryMode.None);

    [Theory]
    [InlineData("full")]
    [InlineData("Full")]
    public void FromCode_ResolvesFull(string code) =>
        HistoryMode.FromCode(code).ShouldBe(HistoryMode.Full);

    [Theory]
    [InlineData("")]
    [InlineData("partial")]
    [InlineData("true")]
    public void FromCode_UnknownValue_Throws(string code) =>
        Should.Throw<ArgumentException>(() => HistoryMode.FromCode(code));

    [Fact]
    public void Json_RoundTrips()
    {
        var json = JsonSerializer.Serialize(HistoryMode.None);
        json.ShouldBe("\"none\"");
        JsonSerializer.Deserialize<HistoryMode>(json).ShouldBe(HistoryMode.None);
    }

    [Fact]
    public void Json_DeserializeUnknownValue_ThrowsArgumentException_NotReflectionWrapper()
    {
        var ex = Should.Throw<ArgumentException>(() => JsonSerializer.Deserialize<HistoryMode>("\"partial\""));
        ex.ShouldNotBeOfType<System.Reflection.TargetInvocationException>();
        ex.Message.ShouldContain("Unknown history mode");
    }

    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    public void Json_DeserializeNonString_ThrowsJsonException(string json) =>
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<HistoryMode>(json));

    [Fact]
    public void Workflow_HistoryOmitted_KeepsFullHistory()
    {
        var workflow = Deserialize(string.Empty);
        workflow.History.ShouldBeNull();
        workflow.SuppressesHistory.ShouldBeFalse();
    }

    [Fact]
    public void Workflow_HistoryNone_SuppressesHistory()
    {
        var workflow = Deserialize("\"history\": \"none\",");
        workflow.History.ShouldBe(HistoryMode.None);
        workflow.SuppressesHistory.ShouldBeTrue();
    }

    [Fact]
    public void Workflow_HistoryFull_DoesNotSuppress() =>
        Deserialize("\"history\": \"full\",").SuppressesHistory.ShouldBeFalse();

    [Fact]
    public void Workflow_HistoryUnderConfig_IsIgnored() =>
        Deserialize("\"config\": { \"history\": \"none\" },").SuppressesHistory.ShouldBeFalse();

    [Fact]
    public void Workflow_History_SurvivesSerializationRoundTrip()
    {
        // The component cache stores the definition as JSON (Redis envelope) and reads it back.
        var workflow = Deserialize("\"history\": \"none\",");
        var json = JsonSerializer.Serialize(workflow, JsonSerializerConstants.JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<WorkflowDefinition>(json, JsonSerializerConstants.JsonOptions)!;
        roundTripped.SuppressesHistory.ShouldBeTrue();

        var envelopeOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        JsonSerializer.Deserialize<WorkflowDefinition>(JsonSerializer.Serialize(workflow, envelopeOptions), envelopeOptions)!
            .SuppressesHistory.ShouldBeTrue();
    }

    private static WorkflowDefinition Deserialize(string historyFragment)
    {
        var json = $$"""
        {
            "type": "F",
            {{historyFragment}}
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
            ]
        }
        """;
        return JsonSerializer.Deserialize<WorkflowDefinition>(json, JsonSerializerConstants.JsonOptions)!;
    }
}
