using System;
using System.Collections.Generic;
using System.Reflection;
using BBT.Workflow.Definitions.GraphQL;
using Npgsql;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions.GraphQL;

public class GraphQLAggregationServiceTests : DomainTestBase<DomainEntryPoint>
{
    [Fact]
    public void BuildSeparatedWhereClauses_ShouldSplitInstanceAndJson_AndQualifyJsonAlias()
    {
        var filter = """{"createdAt":{"dgt":"2024-01-01T00:00:00Z"},"attributes":{"advisor":{"eq":"pm-002"}}}""";
        var node = GraphQLFilterParser.ParseFilter(filter);
        Assert.NotNull(node);

        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        var (jsonWhere, instanceWhere) = GraphQLJsonFilterService.BuildSeparatedWhereClauses(
            node,
            "Data",
            parameters,
            ref parameterIndex,
            dataTableAlias: "d");

        Assert.Contains(@"""d"".""Data""", jsonWhere, StringComparison.Ordinal);
        Assert.Contains(@"s.""CreatedAt""", instanceWhere, StringComparison.Ordinal);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void BuildAggregationSql_ShouldUseJoin_WhenInstanceWhereExists()
    {
        var sql = InvokeBuildAggregationSql(
            "COUNT(*) AS count_result",
            "(\"d\".\"Data\" ->> 'advisor') = {0}",
            "s.\"CreatedAt\" > {1}",
            "(\"d\".\"Data\" ->> 'advisor')",
            "touch",
            true);

        Assert.Contains(@"""touch"".""InstancesData"" d", sql, StringComparison.Ordinal);
        Assert.Contains(@"INNER JOIN ""touch"".""Instances"" s ON s.""Id"" = d.""InstanceId""", sql, StringComparison.Ordinal);
        Assert.Contains(@"WHERE d.""IsLatest"" = true", sql, StringComparison.Ordinal);
        Assert.Contains(@"AND (s.""CreatedAt"" > {1})", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAggregationSql_ShouldKeepSingleTable_WhenOnlyJsonFiltersExist()
    {
        var sql = InvokeBuildAggregationSql(
            "COUNT(*) AS count_result",
            "(\"Data\" ->> 'advisor') = {0}",
            string.Empty,
            "(\"Data\" ->> 'advisor')",
            "touch",
            false);

        Assert.Contains(@"FROM ""touch"".""InstancesData""", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(@"INNER JOIN", sql, StringComparison.Ordinal);
        Assert.Contains(@"WHERE ""IsLatest"" = true", sql, StringComparison.Ordinal);
    }

    private static string InvokeBuildAggregationSql(
        string selectClause,
        string jsonWhereClause,
        string instanceWhereClause,
        string? groupByClause,
        string schema,
        bool requiresJoin)
    {
        var method = typeof(GraphQLAggregationService).GetMethod(
            "BuildAggregationSql",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var result = method!.Invoke(
            null,
            new object?[] { selectClause, jsonWhereClause, instanceWhereClause, groupByClause, schema, requiresJoin });

        Assert.IsType<string>(result);
        return (string)result!;
    }
}
