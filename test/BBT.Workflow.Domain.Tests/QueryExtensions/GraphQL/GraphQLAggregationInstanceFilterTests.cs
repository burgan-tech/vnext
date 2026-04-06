using System.Collections.Generic;
using BBT.Workflow.Definitions.GraphQL;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions.GraphQL;

/// <summary>
/// Ensures aggregation / groupBy SQL joins <c>Instances</c> when filters reference instance columns.
/// </summary>
public class GraphQLAggregationInstanceFilterTests
{
    [Fact]
    public void BuildAggregationSql_WithInstanceColumnFilter_IncludesJoinAndInstanceAlias()
    {
        var filterJson = """{"key":{"eq":"1111"}}""";
        var filterNode = GraphQLFilterParser.ParseFilter(filterJson);
        filterNode.ShouldNotBeNull();

        var parameters = new List<NpgsqlParameter>();
        var index = 0;
        var (jsonWhere, instanceWhere) = GraphQLJsonFilterService.BuildSeparatedWhereClausesForSql(
            filterNode, "Data", parameters, ref index);

        instanceWhere.ShouldNotBeNullOrWhiteSpace();
        instanceWhere.ShouldContain("s.\"Key\"");
        jsonWhere.ShouldBeEmpty();

        var sql = GraphQLAggregationService.BuildAggregationSql(
            "COUNT(*) AS count_result",
            jsonWhere,
            instanceWhere,
            groupByClause: """(\"Data\" ->> 'region')""",
            schema: "myschema");

        sql.ShouldContain("INNER JOIN \"myschema\".\"Instances\" s");
        sql.ShouldContain("FROM \"myschema\".\"InstancesData\" d");
        sql.ShouldContain("WHERE d.\"IsLatest\" = true");
        sql.ShouldContain("GROUP BY");
    }

    [Fact]
    public void BuildAggregationSql_WithJsonOnlyFilter_DoesNotJoinInstances()
    {
        var filterJson = """{"attributes":{"clientId":{"eq":122}}}""";
        var filterNode = GraphQLFilterParser.ParseFilter(filterJson);
        filterNode.ShouldNotBeNull();

        var parameters = new List<NpgsqlParameter>();
        var index = 0;
        var (jsonWhere, instanceWhere) = GraphQLJsonFilterService.BuildSeparatedWhereClausesForSql(
            filterNode, "Data", parameters, ref index);

        instanceWhere.ShouldBeEmpty();
        jsonWhere.ShouldNotBeNullOrWhiteSpace();

        var sql = GraphQLAggregationService.BuildAggregationSql(
            "COUNT(*) AS count_result",
            jsonWhere,
            instanceWhere,
            groupByClause: null,
            schema: "public");

        sql.ShouldNotContain("INNER JOIN");
        sql.ShouldContain("FROM \"public\".\"InstancesData\"");
        sql.ShouldContain("WHERE \"IsLatest\" = true");
    }
}
