using System;
using System.Collections.Generic;
using BBT.Workflow.Definitions.GraphQL;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions.Security;

/// <summary>
/// Verifies GroupBy / aggregation filter SQL uses parameters for user values (Instance JOIN path included).
/// </summary>
public class AggregationFilterSqlInjectionTests
{
    private static string BuildParameterizedAggregationCommandText(
        GraphQLFilterNode? filterNode,
        string? groupByClause,
        string schema = "public")
    {
        var parameters = new List<NpgsqlParameter>();
        var index = 0;
        var (jsonWhere, instanceWhere) = GraphQLJsonFilterService.BuildSeparatedWhereClausesForSql(
            filterNode, "Data", parameters, ref index);

        var sql = GraphQLAggregationService.BuildAggregationSql(
            "COUNT(*) AS count_result",
            jsonWhere,
            instanceWhere,
            groupByClause,
            schema);

        return GraphQLAggregationService.ReplacePlaceholders(sql, parameters.Count);
    }

    /// <summary>
    /// User-controlled filter values must not appear verbatim in command text (bound as parameters).
    /// </summary>
    public static TheoryData<string> SqlInjectionPayloads => new()
    {
        "'; DROP TABLE \"Instances\"; --",
        "1 OR 1=1",
        "x' OR '1'='1",
        "admin'--",
        "'; DELETE FROM \"InstancesData\"; --",
    };

    [Theory]
    [MemberData(nameof(SqlInjectionPayloads))]
    public void InstanceColumnFilter_MaliciousEqValue_MustNotAppearInCommandText(string payload)
    {
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload);
        var filterJson = "{\"key\":{\"eq\":" + payloadJson + "}}";
        var filterNode = GraphQLFilterParser.ParseFilter(filterJson);
        filterNode.ShouldNotBeNull();

        var commandText = BuildParameterizedAggregationCommandText(filterNode, groupByClause: """(\"Data\" ->> 'region')""");

        commandText.ShouldNotContain(payload);
        commandText.ShouldContain("INNER JOIN");
        commandText.ShouldContain("s.\"Key\"");
        commandText.ShouldContain("$1");
    }

    [Theory]
    [MemberData(nameof(SqlInjectionPayloads))]
    public void JsonAttributeFilter_MaliciousEqValue_MustNotAppearInCommandText(string payload)
    {
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload);
        var filterJson = "{\"attributes\":{\"clientRef\":{\"eq\":" + payloadJson + "}}}";
        var filterNode = GraphQLFilterParser.ParseFilter(filterJson);
        filterNode.ShouldNotBeNull();

        var commandText = BuildParameterizedAggregationCommandText(filterNode, groupByClause: null);

        commandText.ShouldNotContain(payload);
        commandText.ShouldNotContain("DROP TABLE");
        commandText.ShouldNotContain("DELETE FROM");
        commandText.ShouldContain("$");
    }

    [Fact]
    public void MixedInstanceAndJsonFilters_MaliciousValues_MustNotAppearInCommandText()
    {
        var drop = "'; DROP TABLE \"Instances\"; --";
        var or1 = "' OR '1'='1";
        var dropJson = System.Text.Json.JsonSerializer.Serialize(drop);
        var orJson = System.Text.Json.JsonSerializer.Serialize(or1);
        var filterJson = "{\"and\":[{\"key\":{\"eq\":" + dropJson + "}},{\"attributes\":{\"note\":{\"eq\":" + orJson + "}}}]}";

        var filterNode = GraphQLFilterParser.ParseFilter(filterJson);
        filterNode.ShouldNotBeNull();

        var commandText = BuildParameterizedAggregationCommandText(filterNode, """(\"Data\" ->> 'region')""");

        commandText.ShouldNotContain(drop);
        commandText.ShouldNotContain(or1);
        commandText.ShouldContain("INNER JOIN");
        commandText.ShouldContain("AND");
    }

    [Fact]
    public void MaliciousJsonFieldName_ShouldRejectDuringClauseBuild()
    {
        var filterJson = """{"attributes":{"evil;field":{"eq":"1"}}}""";
        var filterNode = GraphQLFilterParser.ParseFilter(filterJson);
        filterNode.ShouldNotBeNull();

        var parameters = new List<NpgsqlParameter>();
        var index = 0;

        Should.Throw<ArgumentException>(() =>
            GraphQLJsonFilterService.BuildSeparatedWhereClausesForSql(
                filterNode, "Data", parameters, ref index));
    }

    [Theory]
    [InlineData("x') OR 1=1--")]
    [InlineData("role'; SELECT 1--")]
    public void BuildJsonTextAccessor_MaliciousFieldPath_ShouldReject(string path)
    {
        Should.Throw<ArgumentException>(() =>
            GraphQLAggregationService.BuildJsonTextAccessor(path, "Data"));
    }

    [Fact]
    public void BuildJsonTextAccessor_ValidNested_ProducesAccessor()
    {
        var sql = GraphQLAggregationService.BuildJsonTextAccessor("region.code", "Data");
        sql.ShouldContain("ARRAY['region','code']");
        sql.ShouldContain("\"Data\"");
    }

    [Fact]
    public void BuildJsonTextAccessor_AttributesPrefix_StripsAndValidates()
    {
        var sql = GraphQLAggregationService.BuildJsonTextAccessor("attributes.status", "Data");
        sql.ShouldContain("->> 'status'");
    }
}
