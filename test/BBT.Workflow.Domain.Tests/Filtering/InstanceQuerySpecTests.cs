using System;
using System.Linq;
using System.Text.Json.Nodes;
using BBT.Workflow.Filtering;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Filtering;

/// <summary>
/// Unit tests for the fluent <see cref="InstanceQuery"/> builder + <see cref="InstanceQuerySpec"/>
/// serializer. These assert that the clean C# syntax produces exactly the GraphQL wire JSON the
/// existing list endpoint consumes (filter / groupBy / aggregations / sort). Pure, no database.
/// Assertions parse the produced JSON and inspect structure, so they are robust to key ordering.
/// </summary>
public class InstanceQuerySpecTests
{
    // ---------------------------------------------------------------------
    // Filter serialization
    // ---------------------------------------------------------------------

    [Fact]
    public void Filter_SingleAttributeEquality_ProducesAttributeWrappedCondition()
    {
        var spec = InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .Build();

        var filter = JsonNode.Parse(spec.ToFilterJson()!)!;

        // {"attributes":{"scopeGroup":{"eq":"bireysel-3"}}}
        filter["attributes"]!["scopeGroup"]!["eq"]!.GetValue<string>().ShouldBe("bireysel-3");
    }

    [Fact]
    public void Filter_InstanceColumn_ProducesBareColumnCondition()
    {
        var spec = InstanceQuery.Create()
            .Where("currentState", f => f.Eq("complete"))
            .Build();

        var filter = JsonNode.Parse(spec.ToFilterJson()!)!;

        // {"currentState":{"eq":"complete"}} — no "attributes" wrapper for columns.
        filter["currentState"]!["eq"]!.GetValue<string>().ShouldBe("complete");
        filter["attributes"].ShouldBeNull();
    }

    [Fact]
    public void Filter_MultipleTopLevelWhere_ComposesAsAnd()
    {
        var spec = InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .Where("attributes.transactionDate", f => f.Eq("2026-04-27"))
            .Build();

        var and = JsonNode.Parse(spec.ToFilterJson()!)!["and"]!.AsArray();

        and.Count.ShouldBe(2);
        and[0]!["attributes"]!["scopeGroup"]!["eq"]!.GetValue<string>().ShouldBe("bireysel-3");
        and[1]!["attributes"]!["transactionDate"]!["eq"]!.GetValue<string>().ShouldBe("2026-04-27");
    }

    [Fact]
    public void Filter_AndOfOrGroups_ProducesNestedTree()
    {
        // (city=London OR city=Paris) AND (dept=Research OR age>=30)
        var spec = InstanceQuery.Create()
            .OrGroup(
                q => q.Where("attributes.address.city", f => f.Eq("London")),
                q => q.Where("attributes.address.city", f => f.Eq("Paris")))
            .OrGroup(
                q => q.Where("attributes.employment.department.name", f => f.Eq("Research")),
                q => q.Where("attributes.age", f => f.Ge(30)))
            .Build();

        var and = JsonNode.Parse(spec.ToFilterJson()!)!["and"]!.AsArray();
        and.Count.ShouldBe(2);

        var firstOr = and[0]!["or"]!.AsArray();
        firstOr[0]!["attributes"]!["address"]!["city"]!["eq"]!.GetValue<string>().ShouldBe("London");
        firstOr[1]!["attributes"]!["address"]!["city"]!["eq"]!.GetValue<string>().ShouldBe("Paris");

        var secondOr = and[1]!["or"]!.AsArray();
        secondOr[0]!["attributes"]!["employment"]!["department"]!["name"]!["eq"]!.GetValue<string>().ShouldBe("Research");
        secondOr[1]!["attributes"]!["age"]!["ge"]!.GetValue<int>().ShouldBe(30);
    }

    [Fact]
    public void Filter_Not_WrapsInnerCondition()
    {
        var spec = InstanceQuery.Create()
            .Not(q => q.Where("attributes.status", f => f.Eq("Cancelled")))
            .Build();

        var not = JsonNode.Parse(spec.ToFilterJson()!)!["not"]!;
        not["attributes"]!["status"]!["eq"]!.GetValue<string>().ShouldBe("Cancelled");
    }

    [Fact]
    public void Filter_AllOperators_SerializeToTheirWireKeys()
    {
        var spec = InstanceQuery.Create()
            .Where("attributes.name", f => f.Like("Ad"))
            .Where("attributes.status", f => f.Ne("Cancelled"))
            .Where("attributes.age", f => f.Between(18, 65))
            .Where("attributes.salary", f => f.Gt(50000))
            .Where("attributes.city", f => f.In("London", "Paris"))
            .Where("attributes.email", f => f.EndsWith("@x.com"))
            .Where("attributes.phone", f => f.IsNull(false))
            .Build();

        var and = JsonNode.Parse(spec.ToFilterJson()!)!["and"]!.AsArray();

        and[0]!["attributes"]!["name"]!["like"]!.GetValue<string>().ShouldBe("Ad");
        and[1]!["attributes"]!["status"]!["ne"]!.GetValue<string>().ShouldBe("Cancelled");

        var between = and[2]!["attributes"]!["age"]!["between"]!.AsArray();
        between[0]!.GetValue<int>().ShouldBe(18);
        between[1]!.GetValue<int>().ShouldBe(65);

        and[3]!["attributes"]!["salary"]!["gt"]!.GetValue<int>().ShouldBe(50000);

        var inList = and[4]!["attributes"]!["city"]!["in"]!.AsArray();
        inList.Select(n => n!.GetValue<string>()).ShouldBe(new[] { "London", "Paris" });

        and[5]!["attributes"]!["email"]!["endswith"]!.GetValue<string>().ShouldBe("@x.com");
        and[6]!["attributes"]!["phone"]!["isNull"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void Filter_MultipleOperatorsOnSameField_ComposeAsAnd()
    {
        // age >= 18 AND age < 65 (two ops on one field => two AND leaves)
        var spec = InstanceQuery.Create()
            .Where("attributes.age", f => f.Ge(18).Lt(65))
            .Build();

        var and = JsonNode.Parse(spec.ToFilterJson()!)!["and"]!.AsArray();
        and.Count.ShouldBe(2);
        and[0]!["attributes"]!["age"]!["ge"]!.GetValue<int>().ShouldBe(18);
        and[1]!["attributes"]!["age"]!["lt"]!.GetValue<int>().ShouldBe(65);
    }

    [Fact]
    public void Filter_Includes_SerializesPartialObjectContainment()
    {
        // {"attributes":{"videoCallParticipants":{"includes":{"userId":"adv-1"}}}} — the
        // jsonb-containment condition rezervation queries use for participant matching.
        var spec = InstanceQuery.Create()
            .Where("attributes.videoCallParticipants", f => f.Includes(new { userId = "adv-1" }))
            .Build();

        var filter = JsonNode.Parse(spec.ToFilterJson()!)!;

        filter["attributes"]!["videoCallParticipants"]!["includes"]!["userId"]!
            .GetValue<string>().ShouldBe("adv-1");
    }

    [Fact]
    public void First_WithIncludes_ThrowsBecauseItIsListOnly()
    {
        // The single-resolve SQL engine has no jsonb containment support — fail at build time.
        Should.Throw<InvalidOperationException>(() =>
            InstanceQuery.Create()
                .OrGroup(
                    q => q.Where("attributes.advisorId", f => f.Eq("adv-1")),
                    q => q.Where("attributes.videoCallParticipants", f => f.Includes(new { userId = "adv-1" })))
                .First());
    }

    // ---------------------------------------------------------------------
    // GroupBy + aggregations
    // ---------------------------------------------------------------------

    [Fact]
    public void GroupBy_WithAggregation_NestsAggregationsInsideGroupBy()
    {
        var spec = InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .GroupBy("attributes.scopeGroup", "attributes.limitKey")
            .Sum("attributes.amount")
            .Count()
            .Build();

        var groupBy = JsonNode.Parse(spec.ToGroupByJson()!)!;
        groupBy["fields"]!.AsArray().Select(n => n!.GetValue<string>())
            .ShouldBe(new[] { "attributes.scopeGroup", "attributes.limitKey" });
        groupBy["aggregations"]!["sum"]!.GetValue<string>().ShouldBe("attributes.amount");
        groupBy["aggregations"]!["count"]!.GetValue<bool>().ShouldBeTrue();

        // When grouping, aggregations must NOT also appear as a standalone param.
        spec.ToAggregationsJson().ShouldBeNull();
    }

    [Fact]
    public void Aggregations_WithoutGroupBy_ProduceStandaloneAggregations()
    {
        var spec = InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .Sum("attributes.amount")
            .Build();

        // Standalone aggregations param, no groupBy.
        spec.ToGroupByJson().ShouldBeNull();
        var agg = JsonNode.Parse(spec.ToAggregationsJson()!)!;
        agg["sum"]!.GetValue<string>().ShouldBe("attributes.amount");
    }

    // ---------------------------------------------------------------------
    // Filter request envelope (single filter-parameter value)
    // ---------------------------------------------------------------------

    [Fact]
    public void ToFilterRequestJson_WithoutGroupByOrAggregations_IsThePlainFilterJson()
    {
        var spec = InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .Build();

        // No envelope wrapping — byte-identical to the plain filter value.
        spec.ToFilterRequestJson().ShouldBe(spec.ToFilterJson());
    }

    [Fact]
    public void ToFilterRequestJson_WithGroupBy_WrapsFilterAndGroupByInEnvelope()
    {
        var spec = InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .GroupBy("attributes.limitKey")
            .Sum("attributes.amount")
            .Build();

        var envelope = JsonNode.Parse(spec.ToFilterRequestJson()!)!;

        envelope["filter"]!["attributes"]!["scopeGroup"]!["eq"]!.GetValue<string>().ShouldBe("bireysel-3");
        envelope["groupBy"]!["fields"]!.AsArray().Single()!.GetValue<string>().ShouldBe("attributes.limitKey");
        envelope["groupBy"]!["aggregations"]!["sum"]!.GetValue<string>().ShouldBe("attributes.amount");
        envelope["aggregations"].ShouldBeNull(); // nested in groupBy, never standalone
    }

    [Fact]
    public void ToFilterRequestJson_WithStandaloneAggregations_WrapsAggregationsInEnvelope()
    {
        var spec = InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .Sum("attributes.amount")
            .Build();

        var envelope = JsonNode.Parse(spec.ToFilterRequestJson()!)!;

        envelope["filter"]!["attributes"]!["scopeGroup"]!["eq"]!.GetValue<string>().ShouldBe("bireysel-3");
        envelope["aggregations"]!["sum"]!.GetValue<string>().ShouldBe("attributes.amount");
        envelope["groupBy"].ShouldBeNull();
    }

    [Fact]
    public void ToFilterRequestJson_GroupByWithoutFilter_OmitsFilterKey()
    {
        var spec = InstanceQuery.Create()
            .GroupBy("attributes.limitKey")
            .Count()
            .Build();

        var envelope = JsonNode.Parse(spec.ToFilterRequestJson()!)!;

        envelope["filter"].ShouldBeNull();
        envelope["groupBy"]!["aggregations"]!["count"]!.GetValue<bool>().ShouldBeTrue();
    }

    // ---------------------------------------------------------------------
    // Sort + full query string
    // ---------------------------------------------------------------------

    [Fact]
    public void Sort_Descending_SerializesFieldAndDirection()
    {
        var spec = InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("x"))
            .OrderByDescending("createdAt")
            .Build();

        var fields = JsonNode.Parse(spec.ToSortJson()!)!["fields"]!.AsArray();
        fields[0]!["field"]!.GetValue<string>().ShouldBe("createdAt");
        fields[0]!["direction"]!.GetValue<string>().ShouldBe("desc");
    }

    [Fact]
    public void Sort_OnAttribute_UsesDottedPrefixedName()
    {
        var spec = InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("x"))
            .OrderBy("attributes.transactionDate")
            .Build();

        var fields = JsonNode.Parse(spec.ToSortJson()!)!["fields"]!.AsArray();
        fields[0]!["field"]!.GetValue<string>().ShouldBe("attributes.transactionDate");
        fields[0]!["direction"]!.GetValue<string>().ShouldBe("asc");
    }

    [Fact]
    public void ToQueryString_ComposesPagePageSizeFilterAndGroupBy()
    {
        var spec = InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .GroupBy("attributes.limitKey")
            .Sum("attributes.amount")
            .Build();

        var qs = spec.ToQueryString(page: 2, pageSize: 25);

        qs.ShouldContain("page=2");
        qs.ShouldContain("pageSize=25");
        qs.ShouldContain("filter=");
        qs.ShouldContain("groupBy=");
        qs.ShouldNotContain("aggregations="); // aggregations are nested in groupBy
    }

    [Fact]
    public void Build_WithNoFilter_AllowsMatchAllForGroupOnlyQueries()
    {
        // A pure groupBy/report over all instances: no Where clauses is valid for Build().
        var spec = InstanceQuery.Create()
            .GroupBy("attributes.limitKey")
            .Count()
            .Build();

        spec.ToFilterJson().ShouldBeNull();
        JsonNode.Parse(spec.ToGroupByJson()!)!["aggregations"]!["count"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void First_WithGroupBy_ThrowsBecauseItIsListOnly()
    {
        // GroupBy/aggregations are list features; the single-resolve terminals must reject them.
        Should.Throw<InvalidOperationException>(() =>
            InstanceQuery.Create()
                .Where("attributes.x", f => f.Eq("y"))
                .GroupBy("attributes.limitKey")
                .First());
    }
}
