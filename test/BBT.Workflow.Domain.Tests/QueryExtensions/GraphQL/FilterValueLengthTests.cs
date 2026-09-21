using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Definitions.GraphQL.Validation;
using BBT.Workflow.Security;
using Npgsql;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions.GraphQL;

/// <summary>Issue #934: decoded value limits at the query boundary and direct SQL builders.</summary>
public class FilterValueLengthTests
{
    [Theory]
    [InlineData("eq")]
    [InlineData("ne")]
    [InlineData("gt")]
    [InlineData("ge")]
    [InlineData("lt")]
    [InlineData("le")]
    [InlineData("like")]
    [InlineData("match")]
    [InlineData("startswith")]
    [InlineData("endswith")]
    public void ScalarLimit_IsAppliedToBothFormats(string op)
    {
        foreach (var length in new[] { 999, 1000, 1001 })
        {
            var value = new string('a', length);
            foreach (var filter in new[] { JsonFilter("name", op, value), $"name={op}:{value}" })
            {
                var result = Validate(filter);
                Assert.Equal(length <= InputValidator.MaxValueLength, result.IsValid);
                if (!result.IsValid)
                {
                    var error = Assert.Single(result.Errors);
                    Assert.Equal("filter.valueTooLong", error.Code);
                    Assert.Equal($"filter.name.{op}", error.Target);
                    Assert.Contains("1000", error.Message);
                }
            }
        }
    }

    [Theory]
    [InlineData("in")]
    [InlineData("nin")]
    [InlineData("between")]
    public void ArrayLimit_IsPerElement_NotCombinedLength(string op)
    {
        var first = new string('a', 1000);
        foreach (var length in new[] { 1000, 1001 })
        {
            var second = new string('b', length);
            foreach (var filter in new[] { JsonFilter("name", op, new[] { first, second }), $"name={op}:{first},{second}" })
                Assert.Equal(length == 1000, Validate(filter).IsValid);
        }
    }

    [Theory]
    [InlineData("and")]
    [InlineData("or")]
    [InlineData("not")]
    [InlineData("nested")]
    [InlineData("dotted")]
    [InlineData("envelope")]
    [InlineData("instance-column")]
    public void NestedAndAlternateEntryPoints_CannotBypassLimit(string shape)
    {
        var leaf = JsonFilter("name", "eq", new string('x', 1001));
        var value = JsonSerializer.Serialize(new string('x', 1001));
        var filter = shape switch
        {
            "and" or "or" => $"{{\"{shape}\":[{leaf}]}}",
            "not" => $"{{\"not\":{leaf}}}",
            "nested" => $"{{\"attributes\":{{\"profile\":{{\"name\":{{\"eq\":{value}}}}}}}}}",
            "dotted" => JsonFilter("profile.name", "eq", new string('x', 1001)),
            "envelope" => $"{{\"filter\":{leaf},\"aggregations\":{{\"count\":true}}}}",
            _ => $"{{\"key\":{{\"eq\":{value}}}}}"
        };
        Assert.Contains(Validate(filter).Errors, error => error.Code == "filter.valueTooLong");
    }

    [Fact]
    public void Escaping_DoesNotCountSerializedCharactersAsValueLength()
    {
        var value = new string('"', 1000);
        Assert.True(Validate(JsonFilter("name", "eq", value)).IsValid);
        var parameters = new List<NpgsqlParameter>();
        var index = 0;
        AttributeConditionBuilder.BuildOperatorCondition("name", "eq", value, "Data", parameters, ref index);
        using var pattern = JsonDocument.Parse(Assert.IsType<string>(Assert.Single(parameters).Value));
        Assert.Equal(value, pattern.RootElement.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("eq")]
    [InlineData("ne")]
    [InlineData("in")]
    [InlineData("nin")]
    [InlineData("between")]
    public void DirectBuilders_RejectOversizedValuesBeforeAddingParameters(string op)
    {
        var value = new string('x', 1001);
        var parameters = new List<NpgsqlParameter>();
        var index = 0;
        Assert.Throws<ArgumentException>(() =>
            AttributeConditionBuilder.BuildOperatorCondition("name", op, value, "Data", parameters, ref index));
        Assert.Empty(parameters);
        Assert.Equal(0, index);
        Assert.Throws<ArgumentException>(() =>
            InstanceColumnConditionBuilder.BuildCondition("key", op, value, ref index));
        Assert.Equal(0, index);
        Assert.Throws<ArgumentException>(() =>
            PostgreSqlJsonFilterService.BuildFilteredQuery<object>([$"name={op}:{value}"], "Data", "Instances"));
        var node = GraphQLFilterParser.ParseFilter(JsonFilter("name", op,
            op is "in" or "nin" or "between" ? new object[] { "short", value } : value))!;
        Assert.Throws<ArgumentException>(() =>
            GraphQLJsonFilterService.BuildWhereClause(node, "Data", parameters, ref index));
    }

    [Fact]
    public void InstanceArray_IsValidatedBeforeCommaFlattening()
    {
        // Each comma-delimited part is short, but the single authored array item is too long.
        var node = GraphQLFilterParser.ParseFilter(JsonFilter("key", "in", new[] { new string('x', 600) + "," + new string('y', 600) }))!;
        var parameters = new List<NpgsqlParameter>();
        var index = 0;
        Assert.Throws<ArgumentException>(() => GraphQLJsonFilterService.BuildWhereClause(node, "Data", parameters, ref index));
        Assert.Empty(parameters);
    }

    [Fact]
    public void Includes_KeepsItsStructuredPayloadBudget()
    {
        var filter = JsonFilter("items", "includes", new { name = new string('a', 1100) });
        Assert.True(Validate(filter).IsValid);
        var node = GraphQLFilterParser.ParseFilter(filter)!;
        var parameters = new List<NpgsqlParameter>();
        var index = 0;
        GraphQLJsonFilterService.BuildWhereClause(node, "Data", parameters, ref index);
        Assert.Single(parameters);
    }

    private static string JsonFilter(string field, string op, object value) => JsonSerializer.Serialize(
        new { attributes = new Dictionary<string, object> { [field] = new Dictionary<string, object> { [op] = value } } },
        new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static FilterValidationResult Validate(string filter) =>
        InstanceQueryValidator.Validate(new InstanceQueryValidationRequest { Filter = filter });
}
