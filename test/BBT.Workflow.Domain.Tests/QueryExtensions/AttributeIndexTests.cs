using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Definitions.Schemas;
using Npgsql;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions;

public sealed class AttributeIndexTests
{
    private static SchemaFilterContext Context(bool ready = true, bool enforce = true)
    {
        using var json = JsonDocument.Parse("""
          {"type":"object","properties":{
            "amount":{"type":"number","x-indexed":true,"x-filterOperators":["eq","gte","in","neq"],"x-sortable":true},
            "when":{"type":"string","format":"date-time","x-indexed":true,"x-filterOperators":["gte"]}}}
          """);
        var context = SchemaFilterMetadataResolver.Resolve(json.RootElement)!;
        return new SchemaFilterContext(context.Fields)
        {
            ReadyIndexes = ready ? AttributeIndexDefinition.From(context).Select(d => d.Key).ToHashSet() : new HashSet<string>(),
            EnforceFiltering = enforce
        };
    }

    [Fact]
    public void NumericRange_UsesOnlyReadyProjection_WithoutChangingParameters()
    {
        var parameters = new List<NpgsqlParameter>(); var index = 0;
        var sql = AttributeConditionBuilder.BuildOperatorCondition("amount", "ge", "12.5", "Data", parameters, ref index, Context());
        Assert.Contains(new AttributeIndexDefinition("amount", "numeric").ColumnName, sql);
        Assert.DoesNotContain("::numeric", sql);
        Assert.Equal(12.5m, parameters.Single().Value);
        index = 0; parameters.Clear();
        var fallback = AttributeConditionBuilder.BuildOperatorCondition("amount", "ge", "12.5", "Data", parameters, ref index, Context(false));
        Assert.Contains("->> 'amount')::numeric", fallback);
    }

    [Fact]
    public void MembershipAndSort_KeepTextSemantics_OnNumericFields()
    {
        var parameters = new List<NpgsqlParameter>(); var index = 0;
        var sql = AttributeConditionBuilder.BuildOperatorCondition("amount", "in", new object[] { "01", "1.0" }, "Data", parameters, ref index, Context());
        Assert.Contains(new AttributeIndexDefinition("amount", "text").ColumnName, sql);
        Assert.Equal(new object[] { "01", "1.0" }, parameters.Select(p => p.Value));
        var sort = GraphQLJsonFilterService.BuildOrderByClause(new OrderByRequest { Field = "attributes.amount" }, "public", schemaContext: Context());
        Assert.Contains(new AttributeIndexDefinition("amount", "text").ColumnName, sort);
        Assert.EndsWith("s.\"Id\" ASC", sort);
    }

    [Fact]
    public void DisabledSchemaEnforcement_DoesNotDisableProjection()
    {
        var parameters = new List<NpgsqlParameter>(); var index = 0;
        var sql = GraphQLJsonFilterService.BuildWhereClause(GraphQLFilterParser.ParseFilter("""{"attributes":{"amount":{"gt":12}}}""")!, "Data", parameters, ref index, Context(enforce: false));
        Assert.Contains(new AttributeIndexDefinition("amount", "numeric").ColumnName, sql);
    }

    [Theory]
    [InlineData("eq")]
    [InlineData("ne")]
    public void Containment_IsNotRewritten(string op)
    {
        var parameters = new List<NpgsqlParameter>(); var index = 0;
        var sql = AttributeConditionBuilder.BuildOperatorCondition("amount", op, "1", "Data", parameters, ref index, Context());
        Assert.Contains("\"Data\" @>", sql);
        Assert.DoesNotContain("q_", sql);
        Assert.Equal(2, parameters.Count);
    }

    [Theory]
    [InlineData("""{"properties":{"a":{"type":"array","x-indexed":true}}}""")]
    [InlineData("""{"properties":{"a":{"type":["number","null"],"x-indexed":true}}}""")]
    [InlineData("""{"properties":{"a":{"type":"string","x-indexed":"true"}}}""")]
    [InlineData("""{"properties":{"a":{"type":"array","items":{"properties":{"b":{"type":"number","x-indexed":true}}}}}}""")]
    public void UnsupportedIndexMetadata_IsRejected(string schema)
    {
        using var document = JsonDocument.Parse(schema);
        Assert.Throws<ArgumentException>(() => SchemaFilterMetadataResolver.Resolve(document.RootElement));
    }

    [Fact]
    public void MixedOr_AggregationPreservesBooleanTree()
    {
        var node = GraphQLFilterParser.ParseFilter("""{"or":[{"status":{"eq":"A"}},{"attributes":{"amount":{"gt":10}}}]}""");
        var parameters = new List<NpgsqlParameter>(); var index = 0;
        var (json, instance) = GraphQLJsonFilterService.BuildSeparatedWhereClausesForSql(node, "Data", parameters, ref index);
        Assert.Empty(json);
        Assert.Contains(" OR ", instance);
        Assert.Contains("s.\"Status\"", instance);
        Assert.Contains("'amount'", instance);
    }

    [Fact]
    public void LegacyMembership_KeepsNumericLookingStrings_WhenNormalizedToGraphQL()
    {
        var node = FilterFormatDetector.ConvertLegacyToGraphQL("amount=in:01,1.0")!;
        var parameters = new List<NpgsqlParameter>(); var index = 0;
        GraphQLJsonFilterService.BuildWhereClause(node, "Data", parameters, ref index);
        var (_, legacyParameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<object>(["amount=in:01,1.0"], "Data", "Instances");
        Assert.Equal(new object[] { "01", "1.0" }, parameters.Select(p => p.Value));
        Assert.Equal(legacyParameters.Select(p => p.Value), parameters.Select(p => p.Value));
    }

    [Fact]
    public void LegacyEquality_UsesValidJsonForQuotedValues()
    {
        var (_, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<object>(["name=eq:a\"b"], "Data", "Instances");
        using var pattern = JsonDocument.Parse((string)parameters.Single().Value);
        Assert.Equal("a\"b", pattern.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void RealPropertyNamedAttributes_IsNotConfusedWithApiPrefix()
    {
        var definitions = new[] { new AttributeIndexDefinition("amount", "numeric"), new AttributeIndexDefinition("attributes.amount", "numeric") };
        var context = new SchemaFilterContext(new Dictionary<string, SchemaFieldMetadata>
        {
            ["amount"] = new() { Type = "number", Indexed = true, FilterOperators = ["gt"] },
            ["attributes.amount"] = new() { Type = "number", Indexed = true, FilterOperators = ["gt"] }
        }) { ReadyIndexes = definitions.Select(d => d.Key).ToHashSet() };
        var parameters = new List<NpgsqlParameter>(); var index = 0;
        var sql = AttributeConditionBuilder.BuildOperatorCondition("attributes.amount", "gt", 1, "Data", parameters, ref index, context);
        Assert.Contains(definitions[1].ColumnName, sql);
        Assert.DoesNotContain(definitions[0].ColumnName, sql);
    }
}
