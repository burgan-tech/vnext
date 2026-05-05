using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.ExceptionHandling;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions.GraphQL;

public sealed class GraphQLIncludesFilterTests
{
    [Fact]
    public void SerializeFilterNode_WithIncludes_RoundTripsForGroupByCombinedFilterPath()
    {
        const string filterJson = """
            {"attributes":{"members":{"includes":{"memberId":"ia002","role":"advisor"}}}}
            """;

        var node = GraphQLFilterParser.ParseFilter(filterJson);
        node.ShouldNotBeNull();

        var serialized = JsonSerializer.Serialize(node, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        serialized.ShouldContain("\"includes\"");
        serialized.ShouldContain("\"memberId\"");
        serialized.ShouldNotContain("\\\"memberId\\\"");

        var roundTrip = GraphQLFilterParser.ParseFilter(serialized);
        roundTrip.ShouldNotBeNull();
        roundTrip.Attributes!["members"].Includes.ShouldNotBeNull();
        roundTrip.Attributes["members"].Includes!.Value.GetProperty("memberId").GetString().ShouldBe("ia002");
    }

    [Fact]
    public void ParseFilter_IncludesObject_PopulatesFieldCondition()
    {
        const string filterJson = """
            {"attributes":{"members":{"includes":{"memberId":"ia002","role":"advisor"}}}}
            """;

        var node = GraphQLFilterParser.ParseFilter(filterJson);
        node.ShouldNotBeNull();
        node.NodeType.ShouldBe(FilterNodeType.Condition);
        var cond = node.Attributes!["members"];
        cond.Includes.ShouldNotBeNull();
        cond.Includes!.Value.ValueKind.ShouldBe(JsonValueKind.Object);
        cond.Includes.Value.GetProperty("memberId").GetString().ShouldBe("ia002");
    }

    [Fact]
    public void BuildSeparatedWhereClauses_Includes_ProducesJsonbContainsWithArrayPattern()
    {
        const string filterJson = """
            {"attributes":{"members":{"includes":{"memberId":"ia002","role":"advisor"}}}}
            """;

        var node = GraphQLFilterParser.ParseFilter(filterJson);
        var parameters = new List<NpgsqlParameter>();
        var index = 0;

        var (jsonWhere, _) = GraphQLJsonFilterService.BuildSeparatedWhereClausesForSql(
            node, "Data", parameters, ref index, schemaContext: null);

        jsonWhere.ShouldContain("\"Data\"");
        jsonWhere.ShouldContain("@>");
        parameters.Count.ShouldBe(1);
        var payload = parameters[0].Value?.ToString();
        payload.ShouldNotBeNull();
        payload.ShouldContain("members");
        payload.ShouldContain("[");
        payload.ShouldContain("memberId");
        payload.ShouldContain("ia002");
    }

    [Fact]
    public void BuildSeparatedWhereClauses_Includes_NestedPath_WrapsArrayAtLeaf()
    {
        const string filterJson = """
            {"attributes":{"org":{"members":{"includes":{"memberId":"x"}}}}}
            """;

        var node = GraphQLFilterParser.ParseFilter(filterJson);
        var parameters = new List<NpgsqlParameter>();
        var index = 0;

        var (jsonWhere, _) = GraphQLJsonFilterService.BuildSeparatedWhereClausesForSql(
            node, "Data", parameters, ref index, schemaContext: null);

        jsonWhere.ShouldNotBeNullOrEmpty();
        var payload = parameters[0].Value?.ToString();
        payload.ShouldContain("org");
        payload.ShouldContain("members");
        payload.ShouldContain("[");
        payload.ShouldContain("memberId");
    }

    [Fact]
    public void BuildSeparatedWhereClauses_IncludesWithEqOnSameField_ShouldThrow()
    {
        const string filterJson = """
            {"attributes":{"members":{"includes":{"memberId":"ia002"},"eq":"x"}}}
            """;

        var node = GraphQLFilterParser.ParseFilter(filterJson);
        var parameters = new List<NpgsqlParameter>();
        var index = 0;

        Should.Throw<SchemaFilterValidationException>(() =>
            GraphQLJsonFilterService.BuildSeparatedWhereClausesForSql(
                node, "Data", parameters, ref index, schemaContext: null));
    }

    [Fact]
    public void BuildSeparatedWhereClauses_Includes_NotInSchemaOperators_ShouldThrow()
    {
        const string filterJson = """
            {"attributes":{"members":{"includes":{"memberId":"ia002"}}}}
            """;

        var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "members": {
                  "type": "array",
                  "x-filterOperators": ["eq"]
                }
              }
            }
            """).RootElement;

        var schemaContext = SchemaFilterMetadataResolver.Resolve(schema);
        schemaContext.ShouldNotBeNull();

        var node = GraphQLFilterParser.ParseFilter(filterJson);
        var parameters = new List<NpgsqlParameter>();
        var index = 0;

        Should.Throw<SchemaFilterValidationException>(() =>
            GraphQLJsonFilterService.BuildSeparatedWhereClausesForSql(
                node, "Data", parameters, ref index, schemaContext: schemaContext));
    }

    [Fact]
    public void BuildSeparatedWhereClauses_Includes_AllowedBySchema_ShouldSucceed()
    {
        const string filterJson = """
            {"attributes":{"members":{"includes":{"memberId":"ia002"}}}}
            """;

        var schema = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "members": {
                  "type": "array",
                  "x-filterOperators": ["includes"]
                }
              }
            }
            """).RootElement;

        var schemaContext = SchemaFilterMetadataResolver.Resolve(schema);
        schemaContext.ShouldNotBeNull();

        var node = GraphQLFilterParser.ParseFilter(filterJson);
        var parameters = new List<NpgsqlParameter>();
        var index = 0;

        var (jsonWhere, _) = GraphQLJsonFilterService.BuildSeparatedWhereClausesForSql(
            node, "Data", parameters, ref index, schemaContext: schemaContext);

        jsonWhere.ShouldContain("@>");
        parameters.Count.ShouldBe(1);
    }

    public static TheoryData<string, string> IncludesInjectionPayloads => new()
    {
        { "'; DROP TABLE \"Instances\"; --", "DROP TABLE" },
        { "1 OR 1=1", "1 OR 1=1" }
    };

    [Theory]
    [MemberData(nameof(IncludesInjectionPayloads))]
    public void BuildSeparatedWhereClauses_Includes_MaliciousStringInPayload_NotInSqlLiteral(string payload, string expectedInParameter)
    {
        var payloadJson = JsonSerializer.Serialize(payload);
        var filterJson = "{\"attributes\":{\"members\":{\"includes\":{\"memberId\":" + payloadJson + "}}}}";

        var node = GraphQLFilterParser.ParseFilter(filterJson);
        var parameters = new List<NpgsqlParameter>();
        var index = 0;

        var (jsonWhere, _) = GraphQLJsonFilterService.BuildSeparatedWhereClausesForSql(
            node, "Data", parameters, ref index, schemaContext: null);

        jsonWhere.ShouldNotContain(payload);
        parameters[0].Value?.ToString().ShouldContain(expectedInParameter);
    }
}
