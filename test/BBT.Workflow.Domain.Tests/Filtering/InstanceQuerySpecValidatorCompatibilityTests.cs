using System.Linq;
using BBT.Workflow.Definitions.GraphQL.Validation;
using BBT.Workflow.Filtering;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Filtering;

/// <summary>
/// Proves the first-party fluent builder can never emit a query the boundary validator rejects.
/// </summary>
/// <remarks>
/// <see cref="InstanceQuery"/> is the default import for workflow mapping scripts, so if the
/// builder's wire vocabulary and <see cref="InstanceQueryValidator"/>'s accepted vocabulary drift,
/// authored workflows start failing at runtime with no way for the author to see it at build time.
/// Every operator, terminal and parameter shape the builder can produce is exercised here.
/// </remarks>
public class InstanceQuerySpecValidatorCompatibilityTests
{
    private static void ShouldValidate(InstanceQuerySpec spec)
    {
        // As GetInstancesTask sends it: separate filter and sort strings.
        AssertValid(new InstanceQueryValidationRequest
        {
            Filter = spec.ToFilterJson(),
            Sort = spec.ToSortJson()
        });

        // As the list endpoint accepts it: separate filter/groupBy/aggregations/sort parameters.
        AssertValid(new InstanceQueryValidationRequest
        {
            Filter = spec.ToFilterJson(),
            Sort = spec.ToSortJson(),
            GroupBy = spec.ToGroupByJson(),
            Aggregations = spec.ToAggregationsJson()
        });

        // As the single-parameter envelope.
        AssertValid(new InstanceQueryValidationRequest
        {
            Filter = spec.ToFilterRequestJson(),
            Sort = spec.ToSortJson()
        });
    }

    private static void AssertValid(InstanceQueryValidationRequest request)
    {
        var result = InstanceQueryValidator.Validate(request);

        result.IsValid.ShouldBeTrue(
            $"builder output was rejected: {string.Join(" | ", result.Errors.Select(e => $"{e.Code}: {e.Message}"))}");
    }

    [Fact]
    public void EveryComparisonOperator_ShouldValidate()
    {
        ShouldValidate(InstanceQuery.Create()
            .Where("attributes.a", f => f.Eq(1))
            .Where("attributes.b", f => f.Ne(1))
            .Where("attributes.c", f => f.Gt(1))
            .Where("attributes.d", f => f.Ge(1))
            .Where("attributes.e", f => f.Lt(1))
            .Where("attributes.f", f => f.Le(1))
            .Build());
    }

    [Fact]
    public void EveryTextAndSetOperator_ShouldValidate()
    {
        ShouldValidate(InstanceQuery.Create()
            .Where("attributes.a", f => f.Like("x"))
            .Where("attributes.b", f => f.StartsWith("x"))
            .Where("attributes.c", f => f.EndsWith("x"))
            .Where("attributes.d", f => f.In(1, 2))
            .Where("attributes.e", f => f.NotIn(1, 2))
            .Where("attributes.f", f => f.Between(1, 2))
            .Where("attributes.g", f => f.IsNull(true))
            .Build());
    }

    [Fact]
    public void InstanceColumns_WithoutAttributesPrefix_ShouldValidate()
    {
        ShouldValidate(InstanceQuery.Create()
            .Where("status", f => f.Eq("A"))
            .Where("currentState", f => f.Eq("draft"))
            .Where("createdAt", f => f.Ge("2026-01-01"))
            .Build());
    }

    [Fact]
    public void NestedAttributePath_ShouldValidate()
    {
        ShouldValidate(InstanceQuery.Create()
            .Where("attributes.customer.address.city", f => f.Eq("istanbul"))
            .Build());
    }

    [Fact]
    public void LogicalGroups_ShouldValidate()
    {
        ShouldValidate(InstanceQuery.Create()
            .Where("attributes.a", f => f.Eq(1))
            .OrGroup(or => or
                .Where("attributes.b", f => f.Eq(2))
                .Where("attributes.c", f => f.Eq(3)))
            .Not(not => not.Where("attributes.d", f => f.Eq(4)))
            .Build());
    }

    [Fact]
    public void GroupByWithEveryAggregation_ShouldValidate()
    {
        ShouldValidate(InstanceQuery.Create()
            .Where("attributes.scope", f => f.Eq("retail"))
            .GroupBy("attributes.scope", "attributes.limitKey")
            .Count()
            .Sum("attributes.amount")
            .Avg("attributes.amount")
            .Min("attributes.amount")
            .Max("attributes.amount")
            .OrderByDescending("createdAt")
            .Build());
    }

    [Fact]
    public void MultiKeySort_ShouldValidate()
    {
        ShouldValidate(InstanceQuery.Create()
            .Where("attributes.a", f => f.Eq(1))
            .OrderBy("status")
            .OrderByDescending("attributes.amount")
            .Build());
    }

    [Fact]
    public void FilterOnly_WithoutSortOrGrouping_ShouldValidate()
    {
        ShouldValidate(InstanceQuery.Create().Where("key", f => f.Eq("k-1")).Build());
    }
}
