using System.Linq;
using BBT.Workflow.Filtering;
using BBT.Workflow.Infrastructure.Instances;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Pure unit tests (no database) for <see cref="InstanceFilterSqlBuilder"/> operand-driven cast
/// selection on range operators. The cast must follow the operand's type; operands that are neither
/// numeric nor date-like must produce NO cast — a <c>::numeric</c> default would make PostgreSQL
/// reject alphabetical ranges (e.g. <c>name &gt; 'M'</c>) at runtime.
/// </summary>
public class InstanceFilterSqlBuilderTests
{
    [Fact]
    public void Range_NumericOperand_CastsToNumeric()
    {
        var filter = InstanceQuery.Create()
            .Where("attributes.age", f => f.Gt(30))
            .First();

        var builder = new InstanceFilterSqlBuilder();
        var where = builder.BuildWhere(filter.Root);

        where.ShouldContain("::numeric");
        builder.Parameters.Single().ShouldBe(30);
    }

    [Fact]
    public void Range_DateStringOperand_CastsToTimestamptz()
    {
        var filter = InstanceQuery.Create()
            .Where("attributes.startDateTime", f => f.Ge("2026-07-01T00:00:00Z"))
            .First();

        var builder = new InstanceFilterSqlBuilder();
        var where = builder.BuildWhere(filter.Root);

        where.ShouldContain("::timestamptz");
    }

    [Fact]
    public void Range_AlphabeticalStringOperand_EmitsNoCast()
    {
        // Regression: name > 'M' previously defaulted to ::numeric, which PostgreSQL rejects with
        // "invalid input syntax for type numeric". Plain text comparison is the correct semantic.
        var filter = InstanceQuery.Create()
            .Where("attributes.name", f => f.Gt("M"))
            .First();

        var builder = new InstanceFilterSqlBuilder();
        var where = builder.BuildWhere(filter.Root);

        where.ShouldNotContain("::");
        where.ShouldContain("d.\"Data\" ->> 'name' > {0}");
        builder.Parameters.Single().ShouldBe("M");
    }

    [Fact]
    public void Between_AlphabeticalStrings_EmitsNoCast()
    {
        var filter = InstanceQuery.Create()
            .Where("attributes.surname", f => f.Between("A", "K"))
            .First();

        var builder = new InstanceFilterSqlBuilder();
        var where = builder.BuildWhere(filter.Root);

        where.ShouldNotContain("::");
        where.ShouldContain("BETWEEN {0} AND {1}");
    }

    [Fact]
    public void Eq_StronglyTypedNumericOperand_ComparesInNumericDomain()
    {
        // Eq(30) must match a stored 30.0 — numeric-domain equality, raw (untexted) parameter.
        var filter = InstanceQuery.Create()
            .Where("attributes.age", f => f.Eq(30))
            .First();

        var builder = new InstanceFilterSqlBuilder();
        var where = builder.BuildWhere(filter.Root);

        where.ShouldContain("::numeric = {0}");
        builder.Parameters.Single().ShouldBe(30);
    }

    [Fact]
    public void Eq_NumericLookingString_StaysTextComparison()
    {
        // Regression guard: a customer-number-like string operand must NOT force a numeric cast —
        // rows holding non-numeric text would fail the cast at runtime. Text equality as before.
        var filter = InstanceQuery.Create()
            .Where("attributes.customerNo", f => f.Eq("123"))
            .First();

        var builder = new InstanceFilterSqlBuilder();
        var where = builder.BuildWhere(filter.Root);

        where.ShouldNotContain("::");
        builder.Parameters.Single().ShouldBe("123");
    }

    [Fact]
    public void In_StronglyTypedNumbers_ComparesInNumericDomain()
    {
        var filter = InstanceQuery.Create()
            .Where("attributes.age", f => f.In(30, 40))
            .First();

        var builder = new InstanceFilterSqlBuilder();
        var where = builder.BuildWhere(filter.Root);

        where.ShouldContain("::numeric IN ({0}, {1})");
        builder.Parameters.ShouldBe(new object?[] { 30, 40 });
    }

    [Fact]
    public void OrderBy_Attribute_UsesJsonbAccessorForNativeTypeOrdering()
    {
        // ORDER BY keeps the final segment as jsonb (->) so numbers sort numerically
        // (9 < 20 < 100), not as text ("100" < "20" < "9").
        var order = InstanceFilterSqlBuilder.BuildOrderBy(
            new FilterField(FilterFieldKind.Attribute, "employment.salary"), descending: false);

        order.ShouldBe("d.\"Data\" -> 'employment' -> 'salary' ASC");
    }

    [Fact]
    public void OrderBy_Column_UsesNativeColumn()
    {
        var order = InstanceFilterSqlBuilder.BuildOrderBy(
            new FilterField(FilterFieldKind.Column, "createdAt"), descending: true);

        order.ShouldBe("s.\"CreatedAt\" DESC");
    }
}
