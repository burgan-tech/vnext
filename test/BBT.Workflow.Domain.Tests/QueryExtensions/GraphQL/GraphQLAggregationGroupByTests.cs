using System.Collections.Generic;
using BBT.Workflow.Definitions.GraphQL;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions.GraphQL;

public sealed class GraphQLAggregationGroupByTests
{
    [Fact]
    public void BuildGroupBySelectClause_InstanceColumn_UsesTableAccessorAndNeedsJoin()
    {
        var fields = new List<string> { "createdBy" };
        var aggregations = new AggregationRequest { Count = true };

        var (selectClause, groupByClause, needsInstanceJoin) =
            GraphQLAggregationService.BuildGroupBySelectClause(fields, aggregations, "Data");

        needsInstanceJoin.ShouldBeTrue();
        selectClause.ShouldContain("s.\"CreatedBy\"");
        groupByClause.ShouldContain("s.\"CreatedBy\"");
        selectClause.ShouldContain("COUNT(*)");
    }

    [Fact]
    public void BuildGroupBySelectClause_JsonField_KeepsJsonAccessorAndNoJoinRequired()
    {
        var fields = new List<string> { "attributes.status" };
        var aggregations = new AggregationRequest { Count = true };

        var (selectClause, groupByClause, needsInstanceJoin) =
            GraphQLAggregationService.BuildGroupBySelectClause(fields, aggregations, "Data");

        needsInstanceJoin.ShouldBeFalse();
        selectClause.ShouldContain("\"Data\" ->> 'status'");
        groupByClause.ShouldContain("\"Data\" ->> 'status'");
    }

    [Fact]
    public void BuildGroupBySelectClause_MixedInstanceAndJson_NeedsJoin()
    {
        var fields = new List<string> { "modifiedBy", "attributes.region" };
        var aggregations = new AggregationRequest { Count = true };

        var (_, _, needsInstanceJoin) =
            GraphQLAggregationService.BuildGroupBySelectClause(fields, aggregations, "Data");

        needsInstanceJoin.ShouldBeTrue();
    }

    [Fact]
    public void BuildAggregationSql_GroupByOnlyInstanceColumn_ForceJoin_AddsInnerJoin()
    {
        var sql = GraphQLAggregationService.BuildAggregationSql(
            "s.\"CreatedBy\" AS \"createdBy\", COUNT(*) AS count_result",
            jsonWhereClause: string.Empty,
            instanceWhereClause: null,
            groupByClause: "s.\"CreatedBy\"",
            schema: "wf",
            forceInstanceJoin: true);

        sql.ShouldContain("INNER JOIN \"wf\".\"Instances\" s");
        sql.ShouldContain("FROM \"wf\".\"InstancesData\" d");
        sql.ShouldContain("WHERE d.\"IsLatest\" = true");
        sql.ShouldContain("GROUP BY s.\"CreatedBy\"");
    }
}
