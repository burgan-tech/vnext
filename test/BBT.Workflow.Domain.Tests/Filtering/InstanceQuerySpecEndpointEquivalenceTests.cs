using System.Text.Json;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Filtering;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Filtering;

/// <summary>
/// Proves that the single filter-parameter envelope emitted for GetInstancesTask
/// (<see cref="InstanceQuerySpec.ToFilterRequestJson"/>) is parsed by the list endpoint into exactly
/// the same <see cref="GraphQLFilterRequest"/> as the separate filter/groupBy/aggregations query
/// parameters the InstanceController accepts. The two server paths are:
/// <list type="bullet">
/// <item>envelope: InstanceQueryAppService → <see cref="GraphQLFilterParser.TryParseRequest"/> on
/// input.Filter (OrderBy then overridden from input.Sort) → repository parsed overload →
/// UnifiedFilterService.ExecuteRequestAsync</item>
/// <item>separate params: repository string overload → ApplyFilterWithAggregationsAsync →
/// <see cref="GraphQLFilterParser.ParseRequest"/> → the same ExecuteRequestAsync</item>
/// </list>
/// Equal <see cref="GraphQLFilterRequest"/> therefore means identical execution. Note that when
/// grouping, ExecuteRequestAsync only honors aggregations NESTED inside groupBy (top-level
/// aggregations are consulted only without groupBy) — the envelope nests them accordingly.
/// </summary>
public class InstanceQuerySpecEndpointEquivalenceTests
{
    [Fact]
    public void GroupByEnvelope_ParsesIntoSameRequestAsSeparateQueryParameters()
    {
        var spec = InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .GroupBy("attributes.scopeGroup", "attributes.user", "attributes.scope", "attributes.limitKey")
            .Sum("attributes.amount")
            .OrderByDescending("createdAt")
            .Build();

        // Envelope path: the endpoint detects the request inside the filter parameter,
        // then applies the sort parameter over the envelope's OrderBy.
        GraphQLFilterParser.TryParseRequest(spec.ToFilterRequestJson(), out var envelopeRequest).ShouldBeTrue();
        if (GraphQLFilterParser.ParseOrderBy(spec.ToSortJson()) is { } orderBy)
            envelopeRequest!.OrderBy = orderBy;

        // Separate-params path: filter=, groupBy=, aggregations=, orderBy= as individual values.
        var separateRequest = GraphQLFilterParser.ParseRequest(
            spec.ToFilterJson(), spec.ToGroupByJson(), spec.ToAggregationsJson(), spec.ToSortJson());

        Ser(envelopeRequest!.Filter).ShouldBe(Ser(separateRequest.Filter));
        Ser(envelopeRequest.GroupBy).ShouldBe(Ser(separateRequest.GroupBy));
        Ser(envelopeRequest.Aggregations).ShouldBe(Ser(separateRequest.Aggregations));
        Ser(envelopeRequest.OrderBy).ShouldBe(Ser(separateRequest.OrderBy));

        envelopeRequest.GroupBy!.GetFields().ShouldBe(new[]
        {
            "attributes.scopeGroup", "attributes.user", "attributes.scope", "attributes.limitKey"
        });
        envelopeRequest.GroupBy.Aggregations!.Sum.ShouldBe("attributes.amount");
        // Nested under groupBy only — the sole combination grouped execution honors.
        envelopeRequest.Aggregations.ShouldBeNull();
    }

    [Fact]
    public void GroupByJson_MatchesTheHandWrittenControllerGroupByParameter()
    {
        // The known-valid groupBy query-parameter value accepted by the instances controller.
        const string handWritten =
            """{"fields":["attributes.scopeGroup","attributes.user","attributes.scope","attributes.limitKey"],"aggregations":{"sum":"attributes.amount"}}""";

        var spec = InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .GroupBy("attributes.scopeGroup", "attributes.user", "attributes.scope", "attributes.limitKey")
            .Sum("attributes.amount")
            .Build();

        var fromSpec = GraphQLFilterParser.ParseGroupBy(spec.ToGroupByJson());
        var fromLiteral = GraphQLFilterParser.ParseGroupBy(handWritten);

        fromSpec!.GetFields().ShouldBe(fromLiteral!.GetFields());
        fromSpec.Aggregations!.Sum.ShouldBe(fromLiteral.Aggregations!.Sum);
        Ser(fromSpec).ShouldBe(Ser(fromLiteral));
    }

    [Fact]
    public void StandaloneAggregationsEnvelope_ParsesIntoSameRequestAsSeparateQueryParameters()
    {
        var spec = InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .Sum("attributes.amount")
            .Build();

        GraphQLFilterParser.TryParseRequest(spec.ToFilterRequestJson(), out var envelopeRequest).ShouldBeTrue();

        var separateRequest = GraphQLFilterParser.ParseRequest(
            spec.ToFilterJson(), spec.ToGroupByJson(), spec.ToAggregationsJson());

        Ser(envelopeRequest!.Filter).ShouldBe(Ser(separateRequest.Filter));
        Ser(envelopeRequest.GroupBy).ShouldBe(Ser(separateRequest.GroupBy));
        Ser(envelopeRequest.Aggregations).ShouldBe(Ser(separateRequest.Aggregations));

        envelopeRequest.GroupBy.ShouldBeNull();
        envelopeRequest.Aggregations!.Sum.ShouldBe("attributes.amount");
        envelopeRequest.Aggregations.HasAggregations.ShouldBeTrue();
    }

    [Fact]
    public void IncludesFilter_RoundTripsThroughTheEndpointFilterParser()
    {
        // The advisor-scope query shape from rezervation mappings: primary advisor OR
        // participant containment. The endpoint parses the wire JSON into a
        // GraphQLFilterNode (FieldCondition.Includes) and re-serializes it when combining
        // filters — the includes condition must survive that round trip intact.
        var spec = InstanceQuery.Create()
            .OrGroup(
                q => q.Where("attributes.advisorId", f => f.Eq("adv-1")),
                q => q.Where("attributes.videoCallParticipants", f => f.Includes(new { userId = "adv-1" })))
            .Build();

        var parsed = GraphQLFilterParser.ParseFilter(spec.ToFilterJson());
        parsed.ShouldNotBeNull();
        parsed!.NodeType.ShouldBe(FilterNodeType.Or);

        var roundTripped = System.Text.Json.Nodes.JsonNode.Parse(Ser(parsed))!;
        var branches = roundTripped["or"]!.AsArray();
        branches[0]!["attributes"]!["advisorId"]!["eq"]!.GetValue<string>().ShouldBe("adv-1");
        branches[1]!["attributes"]!["videoCallParticipants"]!["includes"]!["userId"]!
            .GetValue<string>().ShouldBe("adv-1");
    }

    [Fact]
    public void PlainFilter_IsNotDetectedAsEnvelope_SoThePlainPathStaysUnchanged()
    {
        var spec = InstanceQuery.Create()
            .Where("attributes.scopeGroup", f => f.Eq("bireysel-3"))
            .Where("currentState", f => f.Eq("complete"))
            .Build();

        // Without groupBy/aggregations the emitted value is a plain filter node: the endpoint's
        // envelope detection must NOT engage, leaving today's plain-filter path byte-identical.
        GraphQLFilterParser.TryParseRequest(spec.ToFilterRequestJson(), out _).ShouldBeFalse();

        var fromRequestValue = GraphQLFilterParser.ParseFilter(spec.ToFilterRequestJson());
        var fromFilterValue = GraphQLFilterParser.ParseFilter(spec.ToFilterJson());
        Ser(fromRequestValue).ShouldBe(Ser(fromFilterValue));
    }

    // Serializes a parsed request component so two instances can be compared structurally.
    private static string Ser(object? component)
        => component is null ? "null" : JsonSerializer.Serialize(component, component.GetType());
}
