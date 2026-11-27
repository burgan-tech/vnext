using System;
using System.Collections.Generic;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.ExceptionHandling;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// A path the caller may not read as stored (<see cref="SchemaFilterContext.RestrictedPaths"/>: an <c>x-roles</c>
/// path its roles do not open, an <c>x-masking</c> path it is not exempt from) cannot be named in a set query
/// (vnext#1262). The grouped response returns before the per-instance exposure pass, so a group key or a
/// <c>min</c>/<c>max</c> would hand the value out, and a filter or a sort order is an oracle on it. Refused
/// whatever <c>EnforceMasterSchemaFiltering</c> says; a planned row-security leaf is not caller input and passes.
/// </summary>
public sealed class RestrictedPathQueryRejectionTests
{
    private static SchemaFilterContext Context(bool enforce = false) =>
        new(new Dictionary<string, SchemaFieldMetadata>
        {
            // Declared filterable and sortable on purpose: the schema allows the query for the roles that may
            // read the field, and the restriction must win for the ones that may not.
            ["assignedUser"] = new() { FilterOperators = ["eq", "in"], Sortable = true },
            ["processingBranch"] = new() { FilterOperators = ["eq"], Sortable = true }
        })
        {
            EnforceFiltering = enforce,
            RestrictedPaths = new HashSet<string> { "assignedUser", "customer.nationalId", "account.iban" }
        };

    private static (string Json, string Instance) CompileFilter(GraphQLFilterNode node, SchemaFilterContext context)
    {
        var parameters = new List<NpgsqlParameter>();
        var index = 0;
        return GraphQLJsonFilterService.BuildSeparatedWhereClausesForSql(
            node, "Data", parameters, ref index, schemaContext: context);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARestrictedPath_IsNeitherFilterableNorSortable(bool enforce)
    {
        var context = Context(enforce);

        context.IsFieldFilterable("assignedUser").ShouldBeFalse();
        context.IsOperatorAllowed("assignedUser", "eq").ShouldBeFalse();
        context.IsFieldSortable("assignedUser").ShouldBeFalse();
    }

    /// <summary>
    /// An ancestor would return (or contain-match) the restricted child; a descendant is a part of the
    /// restricted value itself.
    /// </summary>
    [Theory]
    [InlineData("customer")]
    [InlineData("customer.nationalId.prefix")]
    [InlineData("assignedUser.id")]
    public void AnAncestorOrADescendantOfARestrictedPath_IsRestrictedToo(string path)
    {
        var context = Context();

        context.IsRestrictedOrRelated(path).ShouldBeTrue();
        context.IsFieldFilterable(path).ShouldBeFalse();
    }

    [Theory]
    [InlineData("processingBranch")]
    [InlineData("customer.name")]
    [InlineData("assignedUserName")]
    [InlineData("assigned")]
    [InlineData("cust")]
    public void ASiblingOrAPrefixLookalike_IsUnaffected(string path)
    {
        var context = Context();

        context.IsRestrictedOrRelated(path).ShouldBeFalse();
        context.IsFieldFilterable(path).ShouldBeTrue();
        context.IsFieldSortable(path).ShouldBeTrue();
    }

    [Fact]
    public void AContextNotBoundToACaller_RestrictsNothing()
    {
        var context = new SchemaFilterContext(new Dictionary<string, SchemaFieldMetadata>()) { EnforceFiltering = false };

        context.RestrictedPaths.ShouldBeEmpty();
        context.IsRestrictedOrRelated("assignedUser").ShouldBeFalse();
        Should.NotThrow(() => context.EnsureNotRestricted("assignedUser", "filtered"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GroupingOnARestrictedPath_IsRefused(bool enforce)
    {
        var ex = Should.Throw<SchemaFilterValidationException>(() => GraphQLAggregationService.BuildGroupBySelectClause(
            ["attributes.assignedUser"], new AggregationRequest { Count = true }, "Data", Context(enforce)));

        ex.Message.ShouldContain("assignedUser");
        ex.Code.ShouldBe(WorkflowErrorCodes.SchemaFilterValidation);
    }

    /// <summary>The measured leak: group on a free field and read the guarded one through <c>max</c>.</summary>
    [Fact]
    public void AggregatingOnARestrictedPath_UnderAFreeGroupKey_IsRefused()
    {
        var context = Context();

        Should.Throw<SchemaFilterValidationException>(() => GraphQLAggregationService.BuildGroupBySelectClause(
            ["attributes.processingBranch"], new AggregationRequest { Max = "assignedUser" }, "Data", context));
        Should.Throw<SchemaFilterValidationException>(() => GraphQLAggregationService.BuildGroupBySelectClause(
            ["attributes.processingBranch"], new AggregationRequest { Min = "attributes.assignedUser" }, "Data", context));
        Should.Throw<SchemaFilterValidationException>(() => GraphQLAggregationService.BuildGroupBySelectClause(
            ["attributes.processingBranch"], new AggregationRequest { Count = "assignedUser" }, "Data", context));
        Should.Throw<SchemaFilterValidationException>(() => GraphQLAggregationService.BuildGroupBySelectClause(
            ["attributes.processingBranch"], new AggregationRequest { Sum = "customer.nationalId" }, "Data", context));
        Should.Throw<SchemaFilterValidationException>(() => GraphQLAggregationService.BuildGroupBySelectClause(
            ["attributes.processingBranch"], new AggregationRequest { Avg = "customer.nationalId" }, "Data", context));
    }

    [Fact]
    public void GroupingAndAggregatingOnFreePaths_StillCompiles()
    {
        var (select, groupBy, _) = GraphQLAggregationService.BuildGroupBySelectClause(
            ["attributes.processingBranch"], new AggregationRequest { Count = true, Max = "customer.name" }, "Data", Context());

        select.ShouldContain("processingBranch");
        groupBy.ShouldContain("processingBranch");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SortingOnARestrictedPath_IsRefused(bool enforce)
    {
        var ex = Should.Throw<SchemaFilterValidationException>(() => GraphQLJsonFilterService.BuildOrderByClause(
            new OrderByRequest { Field = "attributes.assignedUser" }, "public", schemaContext: Context(enforce)));

        ex.Message.ShouldContain("sorted");

        GraphQLJsonFilterService.BuildOrderByClause(
                new OrderByRequest { Field = "attributes.processingBranch" }, "public", schemaContext: Context(enforce))
            .ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The sort path rule accepts padded segments and the accessor trims them, so the restriction must be judged
    /// on the trimmed path: <c>customer . nationalId</c> reads exactly <c>customer.nationalId</c>.
    /// </summary>
    [Theory]
    [InlineData("attributes.customer . nationalId")]
    [InlineData("attributes.customer.nationalId ")]
    [InlineData("attributes. customer .nationalId")]
    [InlineData("attributes.assignedUser .id")]
    [InlineData(" attributes.assignedUser")]
    public void SortingOnARestrictedPath_SpelledWithPaddedSegments_IsRefused(string field)
    {
        Should.Throw<SchemaFilterValidationException>(() => GraphQLJsonFilterService.BuildOrderByClause(
            new OrderByRequest { Field = field }, "public", schemaContext: Context()));
    }

    /// <summary>GroupBy, aggregation and filter paths reject padding outright, before any policy check.</summary>
    [Theory]
    [InlineData("attributes.customer . nationalId")]
    [InlineData("attributes.assignedUser .id")]
    public void GroupingOrFilteringWithPaddedSegments_NeverReachesSql(string field)
    {
        // Refused as an INVALID NAME (ArgumentException from InputValidator.ValidateFieldName), on an unbound
        // context too: the padded spelling never becomes an accessor, so there is nothing for the restriction
        // to miss. Should the validator ever start accepting padding, these fail and the sort-style
        // normalisation has to be added here as well.
        var unbound = new SchemaFilterContext(new Dictionary<string, SchemaFieldMetadata>()) { EnforceFiltering = false };
        foreach (var context in new[] { Context(), unbound })
        {
            Should.Throw<ArgumentException>(() => GraphQLAggregationService.BuildGroupBySelectClause(
                [field], new AggregationRequest { Count = true }, "Data", context));
            Should.Throw<ArgumentException>(() => GraphQLAggregationService.BuildGroupBySelectClause(
                ["attributes.processingBranch"], new AggregationRequest { Max = field }, "Data", context));
            Should.Throw<ArgumentException>(() => CompileFilter(new GraphQLFilterNode
            {
                Attributes = new() { [field["attributes.".Length..]] = new FieldCondition { Eq = "1" } }
            }, context));
        }
    }

    /// <summary>
    /// <c>includes</c> compiles to a containment match on the stored array, through its own branch of the
    /// condition builder. On the restricted path, its parent object or a sub-path it is refused like any filter.
    /// </summary>
    [Theory]
    [InlineData("""{"attributes":{"assignedUser":{"includes":{"id":"U-1"}}}}""")]
    [InlineData("""{"attributes":{"customer":{"includes":{"nationalId":"1"}}}}""")]
    [InlineData("""{"attributes":{"customer.nationalId.parts":{"includes":{"code":"1"}}}}""")]
    public void AnIncludesFilterOnARestrictedPath_IsRefused(string filter)
    {
        var node = GraphQLFilterParser.ParseFilter(filter);
        node.ShouldNotBeNull();

        Should.Throw<SchemaFilterValidationException>(() => CompileFilter(node!, Context()))
            .CallerRestricted.ShouldBeTrue();
        Should.Throw<SchemaFilterValidationException>(() => CompileFilter(node!, Context(enforce: true)));
    }

    [Theory]
    [InlineData("""{"attributes":{"assignedUser":{"eq":"U-1"}}}""")]
    [InlineData("""{"attributes":{"assignedUser":{"in":["U-1","U-2"]}}}""")]
    [InlineData("""{"attributes":{"assignedUser":{"isNull":false}}}""")]
    [InlineData("""{"attributes":{"customer.nationalId":{"eq":"1"}}}""")]
    [InlineData("""{"attributes":{"account":{"iban":{"eq":"TR1"}}}}""")]
    [InlineData("""{"attributes":{"account":{"eq":"x"}}}""")]
    [InlineData("""{"or":[{"attributes":{"processingBranch":{"eq":"B-1"}}},{"attributes":{"assignedUser":{"eq":"U-1"}}}]}""")]
    [InlineData("""{"not":{"attributes":{"assignedUser":{"eq":"U-1"}}}}""")]
    public void FilteringOnARestrictedPath_IsRefused_WhereverItSitsInTheTree(string filter)
    {
        var node = GraphQLFilterParser.ParseFilter(filter);
        node.ShouldNotBeNull();

        Should.Throw<SchemaFilterValidationException>(() => CompileFilter(node!, Context()))
            .Message.ShouldContain("filtered");
        Should.Throw<SchemaFilterValidationException>(() => CompileFilter(node!, Context(enforce: true)));
    }

    /// <summary>
    /// The refusal says it is about THIS caller, on every query part and under either policy switch. The list
    /// endpoint reads the flag to answer a retryable error when the roles behind the restriction were unknown.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARestrictionRefusal_IsMarkedAsCallerSpecific_OnEveryQueryPart(bool enforce)
    {
        var filter = GraphQLFilterParser.ParseFilter("""{"attributes":{"assignedUser":{"eq":"U-1"}}}""")!;

        Should.Throw<SchemaFilterValidationException>(() => CompileFilter(filter, Context(enforce)))
            .CallerRestricted.ShouldBeTrue();
        Should.Throw<SchemaFilterValidationException>(() => GraphQLJsonFilterService.BuildOrderByClause(
                new OrderByRequest { Field = "attributes.assignedUser" }, "public", schemaContext: Context(enforce)))
            .CallerRestricted.ShouldBeTrue();
        Should.Throw<SchemaFilterValidationException>(() => GraphQLAggregationService.BuildGroupBySelectClause(
                ["attributes.assignedUser"], new AggregationRequest { Count = true }, "Data", Context(enforce)))
            .CallerRestricted.ShouldBeTrue();
        Should.Throw<SchemaFilterValidationException>(() => GraphQLAggregationService.BuildGroupBySelectClause(
                ["attributes.processingBranch"], new AggregationRequest { Max = "assignedUser" }, "Data", Context(enforce)))
            .CallerRestricted.ShouldBeTrue();
    }

    /// <summary>A rule that holds for every caller — here an operator the schema does not list — is not marked.</summary>
    [Fact]
    public void ARefusalThatHoldsForEveryCaller_IsNotMarkedAsCallerSpecific()
    {
        var filter = GraphQLFilterParser.ParseFilter("""{"attributes":{"processingBranch":{"in":["B-1"]}}}""")!;

        Should.Throw<SchemaFilterValidationException>(() => CompileFilter(filter, Context(enforce: true)))
            .CallerRestricted.ShouldBeFalse();
    }

    /// <summary>
    /// The object form lowercases nested field names (<c>GraphQLFilterNodeConverter</c>), so
    /// <c>{"customer":{"nationalId":…}}</c> reads the key <c>customer.nationalid</c> — not the restricted
    /// <c>customer.nationalId</c>, whose value it therefore cannot reach. The comparison is ordinal on the key
    /// the SQL actually reads; this pins that the two do not drift apart.
    /// </summary>
    [Fact]
    public void TheObjectFormOfANestedPath_ReadsTheLowercasedKey_WhichIsNotTheRestrictedOne()
    {
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"customer":{"nationalId":{"eq":"1"}}}}""")!;
        var parameters = new List<NpgsqlParameter>();
        var index = 0;

        GraphQLJsonFilterService.BuildSeparatedWhereClausesForSql(
            node, "Data", parameters, ref index, schemaContext: Context());

        parameters.ShouldAllBe(p => p.Value!.ToString()!.Contains("nationalid") && !p.Value.ToString()!.Contains("nationalId"));
    }

    [Fact]
    public void FilteringOnAFreePath_StillCompiles()
    {
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"processingBranch":{"eq":"B-1"}}}""");

        var (json, _) = CompileFilter(node!, Context(enforce: true));

        json.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// A row-security rule may name an <c>x-roles</c> path (a trusted <c>CurrentUser</c> value compared with it):
    /// the planned leaf is policy, and the caller's restriction must not refuse it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APolicyExemptLeafOnARestrictedPath_Compiles(bool enforce)
    {
        var planned = new GraphQLFilterNode
        {
            Attributes = new() { ["assignedUser"] = new FieldCondition { Eq = "U-1" } },
            PolicyExempt = true
        };

        var (json, _) = CompileFilter(planned, Context(enforce));

        json.ShouldNotBeNullOrWhiteSpace();
    }

    /// <summary>The caller's own leaf is still refused when it is ANDed with a planned one.</summary>
    [Fact]
    public void ACallerLeafBesideAPolicyExemptLeaf_IsStillRefused()
    {
        var planned = new GraphQLFilterNode
        {
            Attributes = new() { ["assignedUser"] = new FieldCondition { Eq = "U-1" } },
            PolicyExempt = true
        };
        var callers = GraphQLFilterParser.ParseFilter("""{"attributes":{"assignedUser":{"eq":"U-2"}}}""")!;

        Should.Throw<SchemaFilterValidationException>(() =>
            CompileFilter(new GraphQLFilterNode { And = [callers, planned] }, Context()));
    }

    [Fact]
    public void WithRestrictedPaths_KeepsTheRestOfTheContext()
    {
        var source = new SchemaFilterContext(new Dictionary<string, SchemaFieldMetadata>
        {
            ["amount"] = new() { Type = "number", FilterOperators = ["eq"] }
        })
        {
            EnforceFiltering = false,
            EncryptedPaths = new HashSet<string> { "customer.email" },
            ReadyIndexes = new HashSet<string> { "ix" }
        };

        var bound = source.WithRestrictedPaths(new HashSet<string> { "assignedUser" });

        bound.ShouldNotBeSameAs(source);
        bound.Fields.ShouldBeSameAs(source.Fields);
        bound.EnforceFiltering.ShouldBeFalse();
        bound.EncryptedPaths.ShouldBe(source.EncryptedPaths);
        bound.ReadyIndexes.ShouldBe(source.ReadyIndexes);
        bound.IsRestrictedOrRelated("assignedUser").ShouldBeTrue();
        source.RestrictedPaths.ShouldBeEmpty();
    }
}
