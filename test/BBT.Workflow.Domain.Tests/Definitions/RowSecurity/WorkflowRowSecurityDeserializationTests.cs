using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions.RowSecurity;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.RowSecurity;

/// <summary>
/// Binds <c>attributes.rowSecurity</c> from component JSON onto <see cref="Workflow.RowSecurity"/>.
/// </summary>
public sealed class WorkflowRowSecurityDeserializationTests
{
    private const string WorkflowShell = """
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "states": [
                {
                    "key": "review",
                    "stateType": 2,
                    "versionStrategy": "Minor",
                    "labels": [{"label": "Review", "language": "en"}],
                    "transitions": []
                }
            ],
            "sharedTransitions": [],
            "startTransition": {
                "key": "start",
                "target": "review",
                "versionStrategy": "Minor",
                "triggerType": 0,
                "labels": [{"label": "Start", "language": "en"}]
            }
            __ROW_SECURITY__
        }
        """;

    private static Workflow Deserialize(string? rowSecurity)
    {
        var json = WorkflowShell.Replace("__ROW_SECURITY__", rowSecurity == null ? "" : "," + rowSecurity);
        return JsonSerializer.Deserialize<Workflow>(json, JsonSerializerConstants.JsonOptions)!;
    }

    [Fact]
    public void Workflow_Deserializes_RowSecurity_And_Absent_IsNull()
    {
        var wf = Deserialize("""
            "rowSecurity": {
              "rules": [
                { "roles": ["branch-officer"], "filter": {"attributes":{"branchCode":{"eq":"$.context.Headers.x-branch"}}} },
                { "roles": ["$InstanceStarter"] }
              ]
            }
            """);

        wf.RowSecurity!.Rules!.Count.ShouldBe(2);
        wf.RowSecurity.UnknownMembers.ShouldBeNull();
        wf.RowSecurity.Rules[0].Roles.ShouldBe(new[] { "branch-officer" });
        wf.RowSecurity.Rules[0].Filter!.Value.ValueKind.ShouldBe(JsonValueKind.Object);
        wf.RowSecurity.Rules[0].Audience.ShouldBe(RowRuleAudience.Static);
        wf.RowSecurity.Rules[1].Filter.ShouldBeNull();
        wf.RowSecurity.Rules[1].Audience.ShouldBe(RowRuleAudience.InstanceStarter);

        Deserialize(null).RowSecurity.ShouldBeNull();
    }

    [Fact]
    public void Workflow_RowSecurity_UnknownMembers_AreCaptured()
    {
        var wf = Deserialize("""
            "rowSecurity": {
              "default": "deny",
              "rules": [ { "roles": ["a"], "scope": "x" } ]
            }
            """);

        wf.RowSecurity!.UnknownMembers!.Keys.ShouldBe(new[] { "default" });
        wf.RowSecurity.Rules![0].UnknownMembers!.Keys.ShouldBe(new[] { "scope" });
    }

    [Fact]
    public void Workflow_RowSecurity_RoundTripsThroughSerialization()
    {
        var wf = Deserialize("""
            "rowSecurity": { "rules": [ { "roles": ["a"], "filter": {"attributes":{"x":{"eq":1}}} } ] }
            """);

        var json = JsonSerializer.Serialize(wf, JsonSerializerConstants.JsonOptions);
        var again = JsonSerializer.Deserialize<Workflow>(json, JsonSerializerConstants.JsonOptions)!;

        again.RowSecurity!.Rules!.Count.ShouldBe(1);
        again.RowSecurity.Rules[0].Filter!.Value.GetRawText().ShouldBe("""{"attributes":{"x":{"eq":1}}}""");
    }

    [Theory]
    [InlineData(new[] { "officer", "auditor" }, RowRuleAudience.Static)]
    [InlineData(new[] { "$InstanceStarter" }, RowRuleAudience.InstanceStarter)]
    [InlineData(new[] { "$InstanceBehalfOfStarter" }, RowRuleAudience.InstanceBehalfOfStarter)]
    [InlineData(new[] { "officer", "$InstanceStarter" }, RowRuleAudience.Mixed)]
    [InlineData(new[] { "$InstanceStarter", "$InstanceBehalfOfStarter" }, RowRuleAudience.Mixed)]
    [InlineData(new[] { "$PreviousUser" }, RowRuleAudience.Invalid)]
    [InlineData(new[] { "$user.$.context.Instance.Data.owner" }, RowRuleAudience.Invalid)]
    [InlineData(new[] { " " }, RowRuleAudience.Invalid)]
    [InlineData(new string[0], RowRuleAudience.Invalid)]
    public void Rule_Audience_ClassifiesRoles(string[] roles, RowRuleAudience expected)
    {
        new RowSecurityRule { Roles = roles.ToList() }.Audience.ShouldBe(expected);
    }

    [Fact]
    public void Rule_Audience_NullRoles_IsInvalid()
    {
        new RowSecurityRule().Audience.ShouldBe(RowRuleAudience.Invalid);
    }
}
