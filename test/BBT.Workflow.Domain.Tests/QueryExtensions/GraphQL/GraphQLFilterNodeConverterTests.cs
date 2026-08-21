using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions.GraphQL;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions.GraphQL;

/// <summary>
/// Unit tests for <see cref="GraphQLFilterNodeConverter"/>'s capture of input it cannot execute.
/// </summary>
/// <remarks>
/// The converter cannot throw on an unknown operator — an object-valued property is
/// indistinguishable from a legitimate nested field path such as
/// <c>{"parent":{"child":{"eq":1}}}</c>. Instead it records what it dropped so the boundary
/// validator can reject the request. Before this, an operator like <c>gte</c> vanished, the
/// condition compiled to an empty WHERE clause, and the query returned every row.
/// </remarks>
public class GraphQLFilterNodeConverterTests
{
    [Fact]
    public void Parse_ShouldRecordUnrecognizedOperator_WhenValueIsScalar()
    {
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"amount":{"gte":100}}}""");

        var condition = node!.Attributes!["amount"];
        condition.GetOperators().ShouldBeEmpty();

        var unrecognized = condition.UnrecognizedOperators.ShouldHaveSingleItem();
        unrecognized.Name.ShouldBe("gte");
        unrecognized.ValueKind.ShouldBe(JsonValueKind.Number);
    }

    [Fact]
    public void Parse_ShouldRecordUnrecognizedOperator_WhenSchemaSpellingHasObjectValue()
    {
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"amount":{"gte":{"x":1}}}}""");

        var condition = node!.Attributes!["amount"];
        condition.UnrecognizedOperators.ShouldHaveSingleItem().Name.ShouldBe("gte");
        condition.UnrecognizedOperators![0].ValueKind.ShouldBe(JsonValueKind.Object);

        // Still stored as a nested condition, preserving the previous behavior.
        condition.NestedConditions.ShouldContainKey("gte");
    }

    [Fact]
    public void Parse_ShouldNotRecordUnrecognized_ForLegitimateNestedFieldPath()
    {
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"parent":{"child":{"eq":1}}}}""");

        var condition = node!.Attributes!["parent"];
        condition.UnrecognizedOperators.ShouldBeNull();
        condition.NestedConditions.ShouldContainKey("child");
    }

    [Fact]
    public void Parse_ShouldRecordEmptyOperatorName()
    {
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"amount":{"":1}}}""");

        node!.Attributes!["amount"].UnrecognizedOperators.ShouldHaveSingleItem().Name.ShouldBe("");
    }

    [Fact]
    public void Parse_ShouldRecordUnrecognizedNodeProperty()
    {
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"a":{"eq":1}},"wat":5}""");

        node!.UnrecognizedProperties.ShouldHaveSingleItem().ShouldBe("wat");
    }

    [Fact]
    public void Parse_ShouldNotRecord_ForFullySupportedFilter()
    {
        var node = GraphQLFilterParser.ParseFilter(
            """{"and":[{"attributes":{"a":{"eq":1}}},{"attributes":{"b":{"in":[1,2]}}}]}""");

        node!.UnrecognizedProperties.ShouldBeNull();
        node.And!.SelectMany(child => child.Attributes!.Values)
            .ShouldAllBe(c => c.UnrecognizedOperators == null);
    }

    /// <summary>
    /// The capture collections are diagnostic state, not wire state. They must never be written,
    /// because the repository serializes a parsed node back to JSON and reparses it when combining
    /// filters — leaking them would corrupt that round trip.
    /// </summary>
    [Fact]
    public void Serialize_ShouldNotEmitCaptureCollections()
    {
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"amount":{"gte":100}},"wat":5}""");
        node!.Attributes!["amount"].UnrecognizedOperators.ShouldNotBeNull();
        node.UnrecognizedProperties.ShouldNotBeNull();

        var json = JsonSerializer.Serialize(node);

        json.ShouldNotContain("UnrecognizedOperators");
        json.ShouldNotContain("unrecognizedOperators");
        json.ShouldNotContain("UnrecognizedProperties");
        json.ShouldNotContain("unrecognizedProperties");
    }

    [Fact]
    public void Serialize_ShouldRoundTrip_SupportedFilter()
    {
        const string original = """{"attributes":{"amount":{"ge":100},"status":{"in":["A","B"]}}}""";

        var json = JsonSerializer.Serialize(GraphQLFilterParser.ParseFilter(original));
        var reparsed = GraphQLFilterParser.ParseFilter(json);

        reparsed!.Attributes!["amount"].Ge.ShouldNotBeNull();
        reparsed.Attributes["status"].In!.Length.ShouldBe(2);
    }
}
