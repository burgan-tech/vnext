using System.Text.Json;
using BBT.Workflow.Definitions.GraphQL;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions;

/// <summary>
/// The row-security markers on the filter model (<see cref="GraphQLFilterNode.PolicyExempt"/>,
/// <see cref="GraphQLFilterRequest.RowPredicate"/>) are runtime-only: a client can never bind them.
/// </summary>
public class RowPredicatePolicyExemptTests
{
    [Fact]
    public void PolicyExempt_IsNotBindableFromJson()
    {
        var n = JsonSerializer.Deserialize<GraphQLFilterNode>("""{"policyExempt":true,"attributes":{"a":{"eq":"1"}}}""")!;

        n.PolicyExempt.ShouldBeFalse();
        n.UnrecognizedProperties.ShouldNotBeNull().ShouldContain("policyExempt");
    }

    [Fact]
    public void RowPredicate_IsNotBindableFromJson()
    {
        var r = JsonSerializer.Deserialize<GraphQLFilterRequest>(
            """{"filter":{"attributes":{"a":{"eq":"1"}}},"rowPredicate":{"attributes":{"b":{"eq":"2"}}}}""")!;

        r.RowPredicate.ShouldBeNull();
        r.Filter.ShouldNotBeNull();
    }

    [Fact]
    public void PolicyExempt_IsNotWrittenToJson()
    {
        var json = JsonSerializer.Serialize(new GraphQLFilterNode
        {
            PolicyExempt = true,
            Attributes = new() { ["a"] = new FieldCondition { Eq = "1" } }
        });

        json.ShouldNotContain("policyExempt", Case.Insensitive);
    }
}
