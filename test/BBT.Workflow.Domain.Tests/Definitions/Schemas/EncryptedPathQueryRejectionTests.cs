using System.Collections.Generic;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.ExceptionHandling;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// An <c>x-encryption.type: "encrypt"</c> path holds ciphertext in the database, so no SQL-side query may name it —
/// and it is refused, not answered with zero rows, whatever <c>EnforceMasterSchemaFiltering</c> says.
/// </summary>
public sealed class EncryptedPathQueryRejectionTests
{
    private static SchemaFilterContext Context(bool enforce) => new(new Dictionary<string, SchemaFieldMetadata>())
    {
        EnforceFiltering = enforce,
        EncryptedPaths = new HashSet<string> { "customer.email" },
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnEncryptedPath_IsNeitherFilterableNorSortable(bool enforce)
    {
        var context = Context(enforce);

        context.IsFieldFilterable("customer.email").ShouldBeFalse();
        context.IsOperatorAllowed("customer.email", "eq").ShouldBeFalse();
        context.IsFieldSortable("customer.email").ShouldBeFalse();
    }

    /// <summary>An <c>@&gt;</c> containment on the parent object would reach the encrypted child.</summary>
    [Fact]
    public void AnAncestorOfAnEncryptedPath_IsRefusedToo()
    {
        var context = Context(enforce: false);

        context.IsEncryptedOrAncestor("customer").ShouldBeTrue();
        context.IsFieldFilterable("customer").ShouldBeFalse();
    }

    [Fact]
    public void ASiblingOrAPrefixLookalike_IsUnaffected()
    {
        var context = Context(enforce: false);

        context.IsFieldFilterable("customer.name").ShouldBeTrue();
        context.IsEncryptedOrAncestor("cust").ShouldBeFalse();
        context.IsEncryptedOrAncestor("customer.emailVerified").ShouldBeFalse();
    }

    [Fact]
    public void GroupingOrAggregatingOnAnEncryptedPath_IsRefused()
    {
        var context = Context(enforce: false);

        Should.Throw<SchemaFilterValidationException>(() => GraphQLAggregationService.BuildGroupBySelectClause(
            ["attributes.customer.email"], new AggregationRequest { Count = true }, "Data", context));
        Should.Throw<SchemaFilterValidationException>(() => GraphQLAggregationService.BuildGroupBySelectClause(
            ["attributes.status"], new AggregationRequest { Max = "customer.email" }, "Data", context));
    }
}
