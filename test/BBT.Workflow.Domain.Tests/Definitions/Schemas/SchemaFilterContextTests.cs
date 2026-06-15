using System.Collections.Generic;
using BBT.Workflow.Definitions.Schemas;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Schemas;

public sealed class SchemaFilterContextTests
{
    private static SchemaFilterContext CreateTestContext()
    {
        var fields = new Dictionary<string, SchemaFieldMetadata>
        {
            ["amount"] = new()
            {
                Type = "number",
                FilterOperators = new List<string> { "eq", "gt", "gte", "lt", "lte" },
                Sortable = true,
            },
            ["createdAt"] = new()
            {
                Type = "string",
                FilterOperators = new List<string> { "eq", "gt", "gte", "lt", "lte" },
                Sortable = true,
                DisplayFormat = "dd.MM.yyyy",
            },
            ["customerName"] = new()
            {
                Type = "string",
                FilterOperators = new List<string> { "eq", "contains", "startsWith", "endsWith" },
                Sortable = true,
            },
            ["isActive"] = new()
            {
                Type = "boolean",
                FilterOperators = new List<string> { "eq", "neq" },
                Sortable = false,
            },
            ["readOnly"] = new()
            {
                Type = "string",
                FilterOperators = new List<string>(),
                Sortable = false,
            },
        };
        return new SchemaFilterContext(fields);
    }

    [Theory]
    [InlineData("amount", true)]
    [InlineData("createdAt", true)]
    [InlineData("customerName", true)]
    [InlineData("isActive", true)]
    [InlineData("readOnly", false)]
    [InlineData("nonExistent", false)]
    public void IsFieldFilterable_ReturnsExpected(string field, bool expected)
    {
        var ctx = CreateTestContext();
        ctx.IsFieldFilterable(field).ShouldBe(expected);
    }

    [Theory]
    [InlineData("amount", true)]
    [InlineData("createdAt", true)]
    [InlineData("customerName", true)]
    [InlineData("isActive", false)]
    [InlineData("readOnly", false)]
    [InlineData("nonExistent", false)]
    public void IsFieldSortable_ReturnsExpected(string field, bool expected)
    {
        var ctx = CreateTestContext();
        ctx.IsFieldSortable(field).ShouldBe(expected);
    }

    [Theory]
    [InlineData("amount", "gt", true)]
    [InlineData("amount", "ge", true)]
    [InlineData("amount", "like", false)]
    [InlineData("createdAt", "gt", true)]
    [InlineData("createdAt", "le", true)]
    [InlineData("createdAt", "like", false)]
    [InlineData("customerName", "like", true)]
    [InlineData("customerName", "match", true)]
    [InlineData("customerName", "startswith", true)]
    [InlineData("customerName", "gt", false)]
    [InlineData("isActive", "eq", true)]
    [InlineData("isActive", "ne", true)]
    [InlineData("isActive", "gt", false)]
    [InlineData("readOnly", "eq", false)]
    [InlineData("nonExistent", "eq", false)]
    public void IsOperatorAllowed_ReturnsExpected(string field, string internalOp, bool expected)
    {
        var ctx = CreateTestContext();
        ctx.IsOperatorAllowed(field, internalOp).ShouldBe(expected);
    }

    [Theory]
    [InlineData("ge", "gte")]
    [InlineData("le", "lte")]
    [InlineData("ne", "neq")]
    [InlineData("like", "contains")]
    [InlineData("match", "contains")]
    [InlineData("startswith", "startsWith")]
    [InlineData("endswith", "endsWith")]
    [InlineData("eq", "eq")]
    [InlineData("gt", "gt")]
    [InlineData("lt", "lt")]
    public void ToSchemaOperator_MapsCorrectly(string internalOp, string expectedSchemaOp)
    {
        SchemaFilterContext.ToSchemaOperator(internalOp).ShouldBe(expectedSchemaOp);
    }
}
