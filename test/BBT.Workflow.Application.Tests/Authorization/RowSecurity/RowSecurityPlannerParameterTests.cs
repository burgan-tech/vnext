using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BBT.Aether.Users;
using BBT.Workflow.Definitions.RowSecurity;
using BBT.Workflow.Definitions.Schemas;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Authorization.RowSecurity;

/// <summary>
/// Spec §4 parameter failures (P-*): whole-rule drop, <c>in</c> narrowing, polarity, caps, coercion and the
/// protected-path backstop. Every rule is matched (role <c>r</c>), so "no rows" proves that no fallback widened
/// anything.
/// </summary>
public class RowSecurityPlannerParameterTests
{
    private const string Field = "branchCode";
    private const string CanaryValue = "CANARY-7f3a";

    // ---- P rows -------------------------------------------------------------------------------------------

    [Fact]
    public void P1_MissingHeader_IsNoRows() =>
        Plan(Eq("x-branch-code"), headers: new()).ReturnsNoRows.ShouldBeTrue();

    [Fact]
    public void P2_BlankHeader_IsNoRows() =>
        Plan(Eq("x-branch-code"), headers: new() { ["x-branch-code"] = ["   "] }).ReturnsNoRows.ShouldBeTrue();

    [Fact]
    public void P3_ScalarFedTwoQueryValues_IsAmbiguous() =>
        Plan(EqQuery("branch"), query: new() { ["branch"] = ["BR-001", "BR-002"] })
            .DroppedRules.Single().Reason.ShouldBe(RowRuleDropReasons.Ambiguous);

    [Fact]
    public void P5_In_DropsUnresolvedElement() =>
        Render(Plan(InA("x-b1", "x-b2"), headers: new() { ["x-b1"] = ["BR-001"] }).Predicate)
            .ShouldBe("branchCode in [BR-001]");

    [Fact]
    public void P7_In_KeepsLiteralWhenParameterMissing() =>
        Render(Plan(InA("x-b1", "BR-999"), headers: new()).Predicate).ShouldBe("branchCode in [BR-999]");

    [Fact]
    public void P8_InSplit_TrimsAndDropsBlankSegments() =>
        Render(Plan(InSplit("x-branches", ","), headers: new() { ["x-branches"] = [" BR-001 , BR-002,,"] }).Predicate)
            .ShouldBe("branchCode in [BR-001,BR-002]");

    [Fact]
    public void P9_InSplit_OnlyBlankSegments_IsNoRows() =>
        Plan(InSplit("x-branches", ","), headers: new() { ["x-branches"] = [",,"] }).ReturnsNoRows.ShouldBeTrue();

    [Fact]
    public void P11_WithoutSplit_ALiteralCommaIsOneElement() =>
        Render(Plan(InParam("x-branches"), headers: new() { ["x-branches"] = ["BR-001,BR-003"] }).Predicate)
            .ShouldBe("branchCode in [BR-001,BR-003]");

    [Fact]
    public void P11_WithoutSplit_ALiteralCommaIsOneElement_Count()
    {
        var predicate = Plan(InParam("x-branches"), headers: new() { ["x-branches"] = ["BR-001,BR-003"] }).Predicate!;

        predicate.Attributes![Field].In!.ShouldBe(["BR-001,BR-003"]);
    }

    [Fact]
    public void P11_WithoutSplit_RepeatedValuesAreElements() =>
        Render(Plan(InParam("x-branches"), headers: new() { ["x-branches"] = ["BR-001", " BR-003 ", " "] }).Predicate)
            .ShouldBe("branchCode in [BR-001,BR-003]");

    [Fact]
    public void P13_Nin_WithUnresolvedElement_IsNoRows() =>
        Plan(NinA("x-excl-1", "x-excl-2"), headers: new() { ["x-excl-1"] = ["BR-001"] }).ReturnsNoRows.ShouldBeTrue();

    [Fact]
    public void P17_Between_FedThreeValues_IsArity() =>
        Plan(BetweenA("x-range", "10"), headers: new() { ["x-range"] = ["1", "2"] })
            .DroppedRules.Single().Reason.ShouldBe(RowRuleDropReasons.Arity);

    [Fact]
    public void P17_Between_WithMissingBound_IsNoRows() =>
        Plan(BetweenA("x-from", "x-to"), headers: new() { ["x-from"] = ["1"] }).ReturnsNoRows.ShouldBeTrue();

    [Fact]
    public void P19_NumberFieldFedText_IsCoercionDrop() =>
        Plan(Ge("amount", "x-min"), headers: new() { ["x-min"] = ["abc"] }, schema: AmountNumber)
            .DroppedRules.Single().Reason.ShouldBe(RowRuleDropReasons.Coercion);

    [Fact]
    public void P19_NumberFieldFedNumber_IsCoercedToDecimal()
    {
        var decision = Plan(Ge("amount", "x-min"), headers: new() { ["x-min"] = [" 1.5 "] }, schema: AmountNumber);

        decision.Predicate!.Attributes!["amount"].Ge.ShouldBe(1.5m);
    }

    [Fact]
    public void P19_NoSchema_KeepsString() =>
        Plan(Ge("amount", "x-min"), headers: new() { ["x-min"] = ["abc"] })
            .Predicate!.Attributes!["amount"].Ge.ShouldBe("abc");

    [Fact]
    public void P19_InElementsAreCoerced()
    {
        var decision = Plan(Rule("""{"amount":{"in":{"$param":"$.context.Headers.x-a","split":","}}}"""),
            headers: new() { ["x-a"] = ["1,2"] }, schema: AmountNumber);

        decision.Predicate!.Attributes!["amount"].In!.ShouldBe([1m, 2m]);
    }

    [Fact]
    public void P28_NotIn_WithUnresolvedElement_IsNoRows() =>
        Plan(NotIn("x-b1", "x-b2"), headers: new() { ["x-b1"] = ["BR-001"] }).ReturnsNoRows.ShouldBeTrue();

    [Fact]
    public void P28_DoubleNegatedIn_NarrowsAgain() =>
        Render(Plan(Rule("""{"not":{"not":{"branchCode":{"in":["$.context.Headers.x-b1","$.context.Headers.x-b2"]}}}}"""),
            headers: new() { ["x-b1"] = ["BR-001"] }).Predicate).ShouldBe("not (not (branchCode in [BR-001]))");

    [Fact]
    public void P29_NotEq_Missing_IsNoRows() =>
        Plan(NotEq("x-branch-code"), headers: new()).ReturnsNoRows.ShouldBeTrue();

    [Fact]
    public void P27_OneUnresolvedLeafInAndOr_DropsWholeRule() =>
        Plan(Rule("""{"and":[{"a":{"eq":"1"}},{"or":[{"b":{"eq":"2"}},{"c":{"eq":"$.context.Headers.x-missing"}}]}]}"""))
            .ReturnsNoRows.ShouldBeTrue();

    [Fact]
    public void P24_MoreThanMaxElements_IsCap() =>
        Plan(InSplit("x-b", ","), headers: new() { ["x-b"] = [string.Join(",", Enumerable.Range(0, 101).Select(i => $"B{i}"))] })
            .DroppedRules.Single().Reason.ShouldBe(RowRuleDropReasons.Cap);

    [Fact]
    public void P24_ExactlyMaxElements_IsKept() =>
        Plan(InSplit("x-b", ","), headers: new() { ["x-b"] = [string.Join(",", Enumerable.Range(0, 100).Select(i => $"B{i}"))] })
            .Outcome.ShouldBe(RowSecurityOutcome.Filtered);

    [Fact]
    public void P24_ElementLongerThanMax_IsCap() =>
        Plan(Eq("x-b"), headers: new() { ["x-b"] = [new string('a', 1001)] })
            .DroppedRules.Single().Reason.ShouldBe(RowRuleDropReasons.Cap);

    [Fact]
    public void P24_LongElementUnderIn_DropsRuleNotElement() =>
        Plan(InA("x-b1", "x-b2"), headers: new() { ["x-b1"] = ["BR-001"], ["x-b2"] = [new string('a', 1001)] })
            .DroppedRules.Single().Reason.ShouldBe(RowRuleDropReasons.Cap);

    [Fact]
    public void P24_CapsAreConfigurable() =>
        Plan(InSplit("x-b", ","), headers: new() { ["x-b"] = ["a,b,c"] },
                limits: new RowSecurityOptions { MaxElements = 2 })
            .DroppedRules.Single().Reason.ShouldBe(RowRuleDropReasons.Cap);

    // ---- runtime backstop ----------------------------------------------------------------------------------

    [Fact]
    public void Backstop_HeaderOnMaskedPath_IsProtectedPath() =>
        Plan(Rule("""{"iban":{"eq":"$.context.Headers.x-iban"}}"""), headers: new() { ["x-iban"] = ["TR1"] },
                exposure: Exposure("""{"iban":{"type":"string","x-masking":{"operator":"mask"}}}"""))
            .DroppedRules.Single().Reason.ShouldBe(RowRuleDropReasons.ProtectedPath);

    [Fact]
    public void Backstop_ProtectedPathWinsOverMissing() =>
        Plan(Rule("""{"iban":{"eq":"$.context.Headers.x-iban"}}"""),
                exposure: Exposure("""{"iban":{"type":"string","x-masking":{"operator":"mask"}}}"""))
            .DroppedRules.Single().Reason.ShouldBe(RowRuleDropReasons.ProtectedPath);

    [Fact]
    public void Backstop_LiteralOnEncryptedPath_IsProtectedPath() =>
        Plan(Rule("""{"secret":{"eq":"x"}}"""),
                exposure: Exposure("""{"secret":{"type":"string","x-encryption":{"type":"encrypt"}}}"""))
            .DroppedRules.Single().Reason.ShouldBe(RowRuleDropReasons.ProtectedPath);

    [Fact]
    public void Backstop_QueryOnRoleRestrictedPath_IsProtectedPath() =>
        Plan(Rule("""{"region":{"eq":"$.context.QueryParameters.r"}}"""), query: new() { ["r"] = ["EU"] },
                exposure: Exposure("""{"region":{"type":"string","x-roles":[{"role":"officer","grant":"allow"}]}}"""))
            .DroppedRules.Single().Reason.ShouldBe(RowRuleDropReasons.ProtectedPath);

    [Fact]
    public void Backstop_HeaderOnRoleRestrictedPath_IsWarningOnly_NotDropped() =>
        Render(Plan(Rule("""{"region":{"eq":"$.context.Headers.x-r"}}"""), headers: new() { ["x-r"] = ["EU"] },
                exposure: Exposure("""{"region":{"type":"string","x-roles":[{"role":"officer","grant":"allow"}]}}"""))
            .Predicate).ShouldBe("region eq EU");

    [Fact]
    public void Backstop_OneProtectedLeaf_DropsWholeRule() =>
        Plan(Rule("""{"or":[{"branchCode":{"eq":"BR-1"}},{"iban":{"eq":"x"}}]}"""),
                exposure: Exposure("""{"iban":{"type":"string","x-masking":{"operator":"mask"}}}"""))
            .ReturnsNoRows.ShouldBeTrue();

    // ---- polarity, coercion, names only --------------------------------------------------------------------

    [Fact]
    public void AnyFailureUnderNot_DropsRule()
    {
        // Each case is a failure that would be tolerated or not exist outside `not`.
        string[] filters =
        [
            """{"not":{"branchCode":{"in":["$.context.Headers.x-b1","$.context.Headers.x-b2"]}}}""",
            """{"not":{"branchCode":{"in":{"$param":"$.context.Headers.x-s","split":","}}}}""",
            """{"not":{"branchCode":{"eq":"$.context.Headers.x-missing"}}}""",
            """{"not":{"and":[{"a":{"eq":"1"}},{"branchCode":{"in":["$.context.Headers.x-b2"]}}]}}""",
            """{"or":[{"a":{"eq":"1"}},{"not":{"branchCode":{"in":["$.context.Headers.x-b1","$.context.Headers.x-b2"]}}}]}"""
        ];

        foreach (var filter in filters)
        {
            var decision = Plan(Rule(filter), headers: new() { ["x-b1"] = ["BR-001"], ["x-s"] = ["BR-001,,"] });

            decision.Outcome.ShouldBe(RowSecurityOutcome.NoContribution, filter);
            decision.Predicate.ShouldBeNull(filter);
        }
    }

    [Theory] // U-11
    [InlineData("integer", "42", 42)]
    [InlineData("number", "1.5", 1.5)]
    [InlineData("boolean", "true", true)]
    [InlineData("boolean", "False", false)]
    [InlineData("string", "1.5", "1.5")]
    [InlineData("unknown", "x", "x")]
    [InlineData(null, "x", "x")]
    [InlineData("number", "1,5", null)]
    [InlineData("number", "abc", null)]
    [InlineData("integer", "1.5", null)]
    [InlineData("boolean", "yes", null)]
    public void Coerce(string? type, string raw, object? expected)
    {
        var ok = RowSecurityPlanner.TryCoerce(raw, type, out var value);

        if (expected is null)
        {
            ok.ShouldBeFalse();
            return;
        }

        ok.ShouldBeTrue();
        switch (expected)
        {
            case int or double:
                value.ShouldBeOfType<decimal>().ShouldBe(Convert.ToDecimal(expected, System.Globalization.CultureInfo.InvariantCulture));
                break;
            default:
                value.ShouldBe(expected);
                break;
        }
    }

    [Fact]
    public void NamesOnly_DecisionCarriesNoValues()
    {
        var cases = new[]
        {
            Plan(EqQuery("branch"), query: new() { ["branch"] = [CanaryValue, CanaryValue + "2"] }),          // ambiguous
            Plan(Eq("x-b"), headers: new() { ["x-b"] = [CanaryValue + new string('a', 1001)] }),             // cap
            Plan(Ge("amount", "x-min"), headers: new() { ["x-min"] = [CanaryValue] }, schema: AmountNumber),  // coercion
            Plan(NinA("x-b1", "x-b2"), headers: new() { ["x-b1"] = [CanaryValue] }),                          // missing
            Plan(Eq("x-b"), headers: new() { ["x-b"] = [CanaryValue] })                                       // filtered
        };

        cases[^1].Outcome.ShouldBe(RowSecurityOutcome.Filtered);
        foreach (var decision in cases)
        {
            var json = JsonSerializer.Serialize(decision with { Predicate = null });
            json.ShouldNotContain(CanaryValue);
        }

        cases[0].DroppedRules.Single().ParameterNames.ShouldBe(["branch"]);
        cases[1].DroppedRules.Single().ParameterNames.ShouldBe(["x-b"]);
        cases[2].DroppedRules.Single().ParameterNames.ShouldBe(["x-min"]);
        cases[3].MissingParameterNames.ShouldBe(["x-b2"]);
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private static readonly SchemaFilterContext AmountNumber = SchemaFilterMetadataResolver.Resolve(
        JsonDocument.Parse("""{"type":"object","properties":{"amount":{"type":"number"}}}""").RootElement)!;

    private static SchemaExposureMetadata Exposure(string properties) =>
        SchemaRolesParser.ParseExposure(
            JsonDocument.Parse($$"""{"type":"object","properties":{{properties}}}""").RootElement);

    private static string Header(string name) => $"\"$.context.Headers.{name}\"";

    private static string Elements(IEnumerable<string> items) =>
        string.Join(",", items.Select(i => i.StartsWith("x-", StringComparison.Ordinal) ? Header(i) : $"\"{i}\""));

    private static string Rule(string filter) => filter;

    private static string Eq(string header) => "{\"" + Field + "\":{\"eq\":" + Header(header) + "}}";

    private static string NotEq(string header) => "{\"not\":" + Eq(header) + "}";

    private static string EqQuery(string name) =>
        "{\"" + Field + "\":{\"eq\":\"$.context.QueryParameters." + name + "\"}}";

    private static string Ge(string field, string header) => "{\"" + field + "\":{\"ge\":" + Header(header) + "}}";

    private static string InA(params string[] items) => "{\"" + Field + "\":{\"in\":[" + Elements(items) + "]}}";

    private static string NinA(params string[] items) => "{\"" + Field + "\":{\"nin\":[" + Elements(items) + "]}}";

    private static string NotIn(params string[] items) => "{\"not\":" + InA(items) + "}";

    private static string BetweenA(string a, string b) => "{\"amount\":{\"between\":[" + Elements([a, b]) + "]}}";

    private static string InSplit(string header, string separator) =>
        "{\"" + Field + "\":{\"in\":{\"$param\":\"$.context.Headers." + header + "\",\"split\":\"" + separator + "\"}}}";

    private static string InParam(string header) =>
        "{\"" + Field + "\":{\"in\":{\"$param\":\"$.context.Headers." + header + "\"}}}";

    private static RowSecurityDecision Plan(string filter,
        Dictionary<string, string[]>? headers = null,
        Dictionary<string, string[]>? query = null,
        SchemaFilterContext? schema = null,
        SchemaExposureMetadata? exposure = null,
        RowSecurityOptions? limits = null)
    {
        var definition = JsonSerializer.Deserialize<RowSecurityDefinition>(
            $$"""{"rules":[{"roles":["r"],"filter":{{filter}}}]}""")!;
        var source = new RequestRowParameterSource(headers ?? new(), null, query ?? new(), null,
            Substitute.For<ICurrentUser>());

        return RowSecurityPlanner.Plan(new RowSecurityPlanInput(definition,
            new CallerRoleResolution(["r"], CallerRoleOutcome.Resolved), source, schema,
            exposure ?? SchemaExposureMetadata.Empty, limits ?? new RowSecurityOptions()));
    }

    private static string Render(BBT.Workflow.Definitions.GraphQL.GraphQLFilterNode? node)
    {
        node.ShouldNotBeNull();
        return RowSecurityPlannerTableTests.Render(node);
    }
}
