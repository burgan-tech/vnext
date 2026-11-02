using System;
using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions.Validators;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.RowSecurity;

/// <summary>
/// Schema-blind publish rules for <c>attributes.rowSecurity</c> (RS-E01..E07, E09..E13, E17 and the
/// RS-W warnings) and their hook in <see cref="WorkflowValidator"/>.
/// </summary>
public sealed class RowSecurityDefinitionRulesTests
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

    private static RowSecurityDefinition Def(string json) =>
        JsonSerializer.Deserialize<RowSecurityDefinition>(json, JsonSerializerConstants.JsonOptions)!;

    private static string[] Codes(string json) =>
        RowSecurityDefinitionRules.Validate(Def(json)).Select(f => f.Code).ToArray();

    private static Workflow WorkflowWith(string? rowSecurityJson)
    {
        var json = WorkflowShell.Replace("__ROW_SECURITY__",
            rowSecurityJson is null ? "" : ",\"rowSecurity\":" + rowSecurityJson);
        var workflow = JsonSerializer.Deserialize<Workflow>(json, JsonSerializerConstants.JsonOptions)!;
        workflow.SetReference(new Reference("test-flow", "test-domain", "sys-flows", "1.0.0"));
        return workflow;
    }

    public static TheoryData<string, string> ErrorCases => new()
    {
        { """{"rules":[{"roles":["$PreviousUser"]}]}""", "RS-E01" },
        { """{"rules":[{"roles":["$PreviousBehalfOfUser"]}]}""", "RS-E01" },
        { """{"rules":[{"roles":["$role.$.context.Headers.x"]}]}""", "RS-E02" },
        { """{"rules":[{"roles":["$user.$.context.Instance.Data.owner"]}]}""", "RS-E02" },
        { """{"rules":[{"roles":["$userBehalfOf.customer"]}]}""", "RS-E02" },
        { """{"rules":[{"roles":["branch-staff","$InstanceStarter"]}]}""", "RS-E04" },
        { """{"rules":[{"roles":["$InstanceStarter","$InstanceBehalfOfStarter"]}]}""", "RS-E04" },
        { """{"rules":[{"roles":["$Whatever"]}]}""", "RS-E05" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"x":{"eq":"$InstanceStarter"}}}}]}""", "RS-E06" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"x":{"eq":"$.context.Route.x"}}}}]}""", "RS-E07" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"x":{"eq":"$.context.Query.x"}}}}]}""", "RS-E07" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"x":{"eq":"$.context.CurrentUser.Email"}}}}]}""", "RS-E07" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"x":{"nope":"1"}}}}]}""", "RS-E09" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"x":{"eq":null}}}}]}""", "RS-E09" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"x":{"eq":true}}}}]}""", "RS-E09" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"x":{"in":["a",null]}}}}]}""", "RS-E09" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"x":{"between":[1,false]}}}}]}""", "RS-E09" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"amount":{"between":["$.context.Headers.x-from"]}}}}]}""", "RS-E10" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"x":{"eq":{"$param":"$.context.Headers.h","split":","}}}}}]}""", "RS-E11" },
        { """{"rules":[]}""", "RS-E12" },
        { """{}""", "RS-E12" },
        { """{"default":"allow","rules":[{"roles":["hq-auditor"]}]}""", "RS-E12" },
        { """{"rules":[{"roles":["a"],"other":1}]}""", "RS-E12" },
        { """{"rules":[{}]}""", "RS-E12" },
        { """{"rules":[{"roles":[]}]}""", "RS-E12" },
        { """{"rules":[{"roles":[" "]}]}""", "RS-E12" },
        { """{"rules":[{"roles":["a"],"filter":{"sort":{"x":"asc"}}}]}""", "RS-E13" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"name":{"startswith":"$.context.Headers.x-p"}}}}]}""", "RS-E17" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"name":{"like":{"$param":"$.context.Headers.x-p"}}}}}]}""", "RS-E17" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"tags":{"includes":{"k":"$.context.Headers.x-p"}}}}}]}""", "RS-E17" },
    };

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public void Validate_ReportsExactlyTheExpectedError(string json, string code) =>
        Codes(json).ShouldBe([code]);

    public static TheoryData<string, string> WarningCases => new()
    {
        { """{"rules":[{"roles":["$InstanceStarter"]}]}""", "RS-W2b" },
        { """{"rules":[{"roles":["$InstanceBehalfOfStarter"]}]}""", "RS-W2b" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"createdBy":{"eq":"$.context.CurrentUser.ActorUserName"}}}}]}""", "RS-W2b" },
        { """{"rules":[{"roles":["$InstanceStarter"],"filter":{"attributes":{"createdBy":{"eq":"$InstanceStarter"}}}}]}""", "RS-W2b" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"b":{"eq":"$.context.QueryParameters.b"}}}}]}""", "RS-W6q" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"b":{"in":["x","$.context.QueryParameters.b"]}}}}]}""", "RS-W6q" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"b":{"in":{"$param":"$.context.QueryParameters.b","split":","}}}}}]}""", "RS-W6q" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"b":{"eq":"$.Context.Headers.x"}}}}]}""", "RS-W4" },
        { """{"rules":[{"roles":["a"],"filter":{"attributes":{"b":{"eq":"$.CONTEXT.Headers.x"}}}}]}""", "RS-W4" },
    };

    [Theory]
    [MemberData(nameof(WarningCases))]
    public void Validate_ReportsExactlyTheExpectedWarning(string json, string code)
    {
        var findings = RowSecurityDefinitionRules.Validate(Def(json));

        findings.Select(f => f.Code).ShouldBe([code]);
        findings.ShouldAllBe(f => f.Severity == RowSecurityFindingSeverity.Warning);
    }

    [Theory]
    [InlineData("""{"rules":[{"roles":["branch-officer"],"filter":{"attributes":{"branchCode":{"eq":"$.context.Headers.x-branch"}}}},{"roles":["hq-auditor"]}]}""")]
    [InlineData("""{"rules":[{"roles":["a"],"filter":{"or":[{"attributes":{"x":{"in":["a","b"]}}},{"not":{"attributes":{"y":{"isNull":true}}}}]}}]}""")]
    [InlineData("""{"rules":[{"roles":["a"],"filter":{"attributes":{"x":{"between":[1,"$.context.Headers.h"]}}}}]}""")]
    [InlineData("""{"rules":[{"roles":["a"],"filter":{"attributes":{"x":{"isNull":false},"n":{"startswith":"AB"}}}}]}""")]
    public void Validate_AcceptsWellFormedDeclarations(string json) =>
        RowSecurityDefinitionRules.Validate(Def(json)).ShouldBeEmpty();

    [Fact]
    public void Validate_ReportsEachProblem_WithAMemberPath()
    {
        var findings = RowSecurityDefinitionRules.Validate(Def(
            """{"rules":[{"roles":["ok"]},{"roles":["$PreviousUser"]}]}"""));

        findings.Single().Path.ShouldBe("rowSecurity.rules[1].roles[0]");
    }

    [Fact]
    public void Validator_RejectsInvalidRowSecurity_WithCodeAndMember()
    {
        var result = new WorkflowValidator().Validate(
            WorkflowWith("""{"rules":[{"roles":["$PreviousUser"]}]}"""));

        result.IsValid.ShouldBeFalse();
        var error = result.ValidationErrors.Single(e => e.ErrorMessage!.StartsWith("RS-E01"));
        error.MemberNames.ShouldContain("rowSecurity.rules[0].roles[0]");
    }

    [Fact]
    public void Validator_LogsWarningsWithoutBlocking()
    {
        var result = new WorkflowValidator().Validate(
            WorkflowWith("""{"rules":[{"roles":["$InstanceStarter"]}]}"""));

        result.ValidationErrors.Any(e => e.ErrorMessage!.StartsWith("RS-")).ShouldBeFalse();
        result.Warnings.Single(w => w.ErrorMessage!.StartsWith("RS-W2b"))
            .MemberNames.ShouldContain("rowSecurity.rules[0]");
    }

    [Fact]
    public void Validator_AddsNothing_WhenRowSecurityIsAbsent()
    {
        var result = new WorkflowValidator().Validate(WorkflowWith(null));

        result.ValidationErrors.Any(e => e.ErrorMessage!.StartsWith("RS-")).ShouldBeFalse();
        result.Warnings.Any(w => w.ErrorMessage!.StartsWith("RS-")).ShouldBeFalse();
    }

    /// <summary>
    /// A combinator object in <c>roles</c> (RS-E03's shape) does not bind to <c>List&lt;string&gt;</c>:
    /// the Workflow JSON fails to deserialize, so it never reaches the validator.
    /// </summary>
    [Fact]
    public void WorkflowJson_WithCombinatorObjectInRoles_FailsToDeserialize()
    {
        var json = WorkflowShell.Replace("__ROW_SECURITY__",
            ""","rowSecurity":{"rules":[{"roles":[{"allOf":["a","b"]}]}]}""");

        Should.Throw<JsonException>(() =>
            JsonSerializer.Deserialize<Workflow>(json, JsonSerializerConstants.JsonOptions));
    }
}
