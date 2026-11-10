using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions.RowSecurity;
using Shouldly;
using Xunit;
using static BBT.Workflow.Definitions.RowSecurity.RowTemplateValue;

namespace BBT.Workflow.Definitions.RowSecurity;

/// <summary>
/// Unit tests for <see cref="RowFilterTemplateParser"/>: raw rule-filter JSON into a typed template.
/// </summary>
public sealed class RowFilterTemplateParserTests
{
    private static Result<RowFilterTemplate> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return RowFilterTemplateParser.Parse(document.RootElement);
    }

    private static void ShouldFailWith(string json, string code)
    {
        var result = Parse(json);
        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(code);
    }

    private static void ShouldBeLiteral(RowTemplateValue value, JsonValueKind kind, string rawText)
    {
        var literal = value.ShouldBeOfType<Literal>();
        literal.Value.ValueKind.ShouldBe(kind);
        literal.Value.GetRawText().ShouldBe(rawText);
    }

    [Fact]
    public void Parse_FormC_SplitParam_InIn()
    {
        var template = Parse("""{"attributes":{"branchCode":{"in":{"$param":"$.context.Headers.x-branches","split":","}}}}""").Value!;

        var leaf = template.Root.ShouldBeOfType<RowTemplateLeaf>();
        leaf.Field.ShouldBe("branchCode");
        leaf.IsInstanceColumn.ShouldBeFalse();
        leaf.Operator.ShouldBe("in");
        leaf.Value.ShouldBe(new SplitParameter(new(RowParameterSource.Headers, "x-branches"), ","));
    }

    [Fact]
    public void Parse_FormC_ParamWithoutSplit_InIn_HasNullSeparator()
    {
        var template = Parse("""{"attributes":{"branchCode":{"nin":{"$param":"$.context.QueryParameters.branches"}}}}""").Value!;

        template.Root.ShouldBeOfType<RowTemplateLeaf>().Value
            .ShouldBe(new SplitParameter(new(RowParameterSource.QueryParameters, "branches"), null));
    }

    [Fact]
    public void Parse_FormA_MixedElements()
    {
        var template = Parse("""{"attributes":{"branchCode":{"in":["$.context.Headers.x-b1","999"]}}}""").Value!;

        var elements = template.Root.ShouldBeOfType<RowTemplateLeaf>().Value.ShouldBeOfType<Elements>();
        elements.Items.Count.ShouldBe(2);
        elements.Items[0].ShouldBe(new Parameter(new(RowParameterSource.Headers, "x-b1")));
        ShouldBeLiteral(elements.Items[1], JsonValueKind.String, "\"999\"");
    }

    [Fact]
    public void Parse_FormB_ScalarParameterString()
    {
        var template = Parse("""{"attributes":{"region":{"eq":"$.context.CurrentUser.Position"}}}""").Value!;

        template.Root.ShouldBeOfType<RowTemplateLeaf>().Value
            .ShouldBe(new Parameter(new(RowParameterSource.CurrentUser, "Position")));
    }

    [Fact]
    public void Parse_ParamObjectOnScalarOperator_IsParameter()
    {
        var template = Parse("""{"attributes":{"region":{"eq":{"$param":"$.context.Headers.x-region"}}}}""").Value!;

        template.Root.ShouldBeOfType<RowTemplateLeaf>().Value
            .ShouldBe(new Parameter(new(RowParameterSource.Headers, "x-region")));
    }

    [Fact]
    public void Parse_LiteralNumber_KeepsRawJson()
    {
        var template = Parse("""{"attributes":{"amount":{"gt":1000}}}""").Value!;

        ShouldBeLiteral(template.Root.ShouldBeOfType<RowTemplateLeaf>().Value, JsonValueKind.Number, "1000");
    }

    [Fact]
    public void Parse_NearMissContextPrefix_IsLiteral()
    {
        var template = Parse("""{"attributes":{"region":{"eq":"$.Context.Headers.x"}}}""").Value!;

        ShouldBeLiteral(template.Root.ShouldBeOfType<RowTemplateLeaf>().Value, JsonValueKind.String, "\"$.Context.Headers.x\"");
    }

    [Fact]
    public void Parse_InstanceStarterShorthand_OnCreatedBy_IsActorUserName()
    {
        var template = Parse("""{"attributes":{"createdBy":{"eq":"$InstanceStarter"}}}""").Value!;

        var leaf = template.Root.ShouldBeOfType<RowTemplateLeaf>();
        leaf.IsInstanceColumn.ShouldBeTrue();
        leaf.Value.ShouldBe(new Parameter(new(RowParameterSource.CurrentUser, "ActorUserName")));
    }

    [Fact]
    public void Parse_InstanceBehalfOfStarterShorthand_OnCreatedByBehalfOf_IsUserName()
    {
        var template = Parse("""{"attributes":{"createdByBehalfOf":{"eq":"$InstanceBehalfOfStarter"}}}""").Value!;

        template.Root.ShouldBeOfType<RowTemplateLeaf>().Value
            .ShouldBe(new Parameter(new(RowParameterSource.CurrentUser, "UserName")));
    }

    [Fact]
    public void Parse_InstanceStarterShorthand_OnOtherField_FailsRS_E06()
    {
        ShouldFailWith("""{"attributes":{"ownerId":{"eq":"$InstanceStarter"}}}""", "RS-E06");
    }

    [Fact]
    public void Parse_BehalfOfShorthand_OnCreatedBy_FailsRS_E06()
    {
        ShouldFailWith("""{"attributes":{"createdBy":{"in":["$InstanceBehalfOfStarter"]}}}""", "RS-E06");
    }

    [Theory]
    [InlineData("""{"attributes":{"a":{"eq":1}},"sort":{"field":"a"}}""")]
    [InlineData("""{"and":[{"attributes":{"a":{"eq":1}}},{"groupBy":{"field":"a"}}]}""")]
    [InlineData("""{"aggregations":{"count":true}}""")]
    [InlineData("""{"not":{"orderBy":{"field":"a"}}}""")]
    public void Parse_SortInsideFilter_FailsRS_E13(string json)
    {
        ShouldFailWith(json, "RS-E13");
    }

    [Fact]
    public void Parse_UnknownOperator_FailsRS_E09()
    {
        ShouldFailWith("""{"attributes":{"region":{"eqq":"x"}}}""", "RS-E09");
    }

    [Theory]
    [InlineData("""{"attributes":{"region":{"eq":["a"]}}}""")]
    [InlineData("""{"attributes":{"region":{"in":"a"}}}""")]
    [InlineData("""{"attributes":{"region":{"isNull":"yes"}}}""")]
    [InlineData("""{"attributes":{"region":{"includes":"x"}}}""")]
    [InlineData("""{"attributes":{"region":{}}}""")]
    [InlineData("""{"attributes":{}}""")]
    [InlineData("""{"and":[]}""")]
    [InlineData("""{"and":[{"attributes":{"a":{"eq":1}}}],"or":[{"attributes":{"a":{"eq":1}}}]}""")]
    [InlineData("""{"attributes":{"region":{"eq":{"$param":"$.context.Headers.x","extra":1}}}}""")]
    [InlineData("""[]""")]
    public void Parse_InvalidShape_FailsRS_E09(string json)
    {
        ShouldFailWith(json, "RS-E09");
    }

    [Theory]
    [InlineData("""{"attributes":{"region":{"eq":"$.context.Route.x"}}}""")]
    [InlineData("""{"attributes":{"region":{"eq":{"$param":"$.context.Query.x"}}}}""")]
    [InlineData("""{"attributes":{"region":{"in":["$.context.CurrentUser.Email"]}}}""")]
    [InlineData("""{"attributes":{"region":{"in":{"$param":"not-a-context-path"}}}}""")]
    public void Parse_MalformedParameter_FailsRS_E07(string json)
    {
        ShouldFailWith(json, "RS-E07");
    }

    [Theory]
    [InlineData("""{"attributes":{"amount":{"between":[1]}}}""")]
    [InlineData("""{"attributes":{"amount":{"between":[1,2,3]}}}""")]
    [InlineData("""{"attributes":{"amount":{"between":1}}}""")]
    public void Parse_BetweenArity_FailsRS_E10(string json)
    {
        ShouldFailWith(json, "RS-E10");
    }

    [Fact]
    public void Parse_Between_TwoElements_IsElements()
    {
        var template = Parse("""{"attributes":{"amount":{"between":[1,"$.context.QueryParameters.max"]}}}""").Value!;

        var elements = template.Root.ShouldBeOfType<RowTemplateLeaf>().Value.ShouldBeOfType<Elements>();
        ShouldBeLiteral(elements.Items[0], JsonValueKind.Number, "1");
        elements.Items[1].ShouldBe(new Parameter(new(RowParameterSource.QueryParameters, "max")));
    }

    [Theory]
    [InlineData("""{"attributes":{"region":{"eq":{"$param":"$.context.Headers.x","split":","}}}}""")]
    [InlineData("""{"attributes":{"region":{"in":[{"$param":"$.context.Headers.x","split":","}]}}}""")]
    public void Parse_SplitOnScalarOperator_FailsRS_E11(string json)
    {
        ShouldFailWith(json, "RS-E11");
    }

    [Fact]
    public void Parse_LogicalTree_And_Or_Not()
    {
        var template = Parse("""
            {"or":[
              {"attributes":{"createdBy":{"eq":"$InstanceStarter"}}},
              {"and":[{"attributes":{"branchCode":{"eq":"$.context.Headers.x-branch"}}},
                      {"not":{"attributes":{"status":{"eq":"C"}}}}]}
            ]}
            """).Value!;

        var or = template.Root.ShouldBeOfType<RowTemplateOr>();
        or.Children.Count.ShouldBe(2);
        or.Children[0].ShouldBeOfType<RowTemplateLeaf>().Field.ShouldBe("createdBy");
        var and = or.Children[1].ShouldBeOfType<RowTemplateAnd>();
        and.Children.Count.ShouldBe(2);
        var not = and.Children[1].ShouldBeOfType<RowTemplateNot>();
        var statusLeaf = not.Child.ShouldBeOfType<RowTemplateLeaf>();
        statusLeaf.IsInstanceColumn.ShouldBeTrue();
    }

    [Fact]
    public void Parse_MultipleFieldsAndOperators_BecomeAnd()
    {
        var template = Parse("""{"attributes":{"amount":{"ge":1,"le":5},"region":{"eq":"x"}}}""").Value!;

        var and = template.Root.ShouldBeOfType<RowTemplateAnd>();
        and.Children.Count.ShouldBe(3);
        and.Children.ShouldAllBe(c => c is RowTemplateLeaf);
    }

    [Fact]
    public void Parse_OperatorCaseInsensitive_IsCanonical()
    {
        var template = Parse("""{"attributes":{"region":{"ISNULL":true}}}""").Value!;

        var leaf = template.Root.ShouldBeOfType<RowTemplateLeaf>();
        leaf.Operator.ShouldBe("isNull");
        ShouldBeLiteral(leaf.Value, JsonValueKind.True, "true");
    }

    [Fact]
    public void Parse_Includes_IsLiteralObject()
    {
        var template = Parse("""{"attributes":{"owners":{"includes":{"id":"a"}}}}""").Value!;

        ShouldBeLiteral(template.Root.ShouldBeOfType<RowTemplateLeaf>().Value, JsonValueKind.Object, """{"id":"a"}""");
    }

    [Fact]
    public void Parse_RootLevelField_IsAccepted()
    {
        var template = Parse("""{"key":{"eq":"k-1"}}""").Value!;

        var leaf = template.Root.ShouldBeOfType<RowTemplateLeaf>();
        leaf.Field.ShouldBe("key");
        leaf.IsInstanceColumn.ShouldBeTrue();
    }

    [Fact]
    public void Parse_Literal_SurvivesDocumentDisposal()
    {
        var template = Parse("""{"attributes":{"region":{"eq":"x"}}}""").Value!;

        // The source JsonDocument is disposed inside Parse(json); the literal must be a clone.
        ShouldBeLiteral(template.Root.ShouldBeOfType<RowTemplateLeaf>().Value, JsonValueKind.String, "\"x\"");
    }
}
