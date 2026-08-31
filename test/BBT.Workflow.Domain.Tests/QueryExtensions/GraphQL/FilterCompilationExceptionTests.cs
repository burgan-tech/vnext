using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.ExceptionHandling;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions.GraphQL;

/// <summary>
/// Pins the split between the two fail-closed exception types.
/// </summary>
/// <remarks>
/// <see cref="FilterCompilationException"/> means <c>InstanceQueryValidator</c> and the SQL builder
/// disagree about what is executable — a runtime defect worth an Error-level log.
/// <see cref="SchemaFilterValidationException"/> means the master schema rejected a well-formed
/// request, which is routine and must not raise that alarm. <c>InstanceQueryAppService</c> catches
/// only the former; if these types are ever collapsed back into one, the drift log fires on every
/// policy rejection and stops meaning anything.
/// </remarks>
public sealed class FilterCompilationExceptionTests
{
    private static SchemaFilterContext ResolveContext(string schemaJson)
    {
        var context = SchemaFilterMetadataResolver.Resolve(JsonDocument.Parse(schemaJson).RootElement);
        context.ShouldNotBeNull();
        return context!;
    }

    [Fact]
    public void BuildOrderByClause_UnknownSortField_ShouldThrowCompilationException()
    {
        // sort.unknownField is a boundary-validator rule; reaching the builder means drift.
        var orderBy = new OrderByRequest { Field = "notAColumn", Direction = "asc" };

        Should.Throw<FilterCompilationException>(() =>
            GraphQLJsonFilterService.BuildOrderByClause(orderBy, "public"));
    }

    [Fact]
    public void BuildOrderByClause_UnsafeAttributesPath_ShouldThrowCompilationException()
    {
        // sort.unsafePath is likewise owned by the boundary validator.
        var orderBy = new OrderByRequest { Field = "attributes.musteri-no", Direction = "asc" };

        Should.Throw<FilterCompilationException>(() =>
            GraphQLJsonFilterService.BuildOrderByClause(orderBy, "public"));
    }

    [Fact]
    public void BuildOrderByClause_NonSortableField_ShouldThrowSchemaValidationException()
    {
        // Sortability lives in the master schema and is deliberately NOT duplicated in the
        // validator, so this is an expected rejection — not drift.
        var schemaContext = ResolveContext("""
            {
              "type": "object",
              "properties": {
                "amount": { "type": "number", "x-sortable": false }
              }
            }
            """);

        var orderBy = new OrderByRequest { Field = "attributes.amount", Direction = "asc" };

        var ex = Should.Throw<SchemaFilterValidationException>(() =>
            GraphQLJsonFilterService.BuildOrderByClause(orderBy, "public", schemaContext: schemaContext));

        ex.ShouldNotBeOfType<FilterCompilationException>();
    }

    [Fact]
    public void BuildSeparatedWhereClauses_NonFilterableField_ShouldThrowSchemaValidationException()
    {
        // Same rule on the filter side: a schema policy rejection must not be reported as drift.
        var schemaContext = ResolveContext("""
            {
              "type": "object",
              "properties": {
                "secret": { "type": "string", "x-filterable": false }
              }
            }
            """);

        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"secret":{"eq":"x"}}}""");
        var parameters = new List<NpgsqlParameter>();
        var index = 0;

        Should.Throw<SchemaFilterValidationException>(() =>
            GraphQLJsonFilterService.BuildSeparatedWhereClausesForSql(
                node, "Data", parameters, ref index, schemaContext: schemaContext));
    }

    [Fact]
    public void FilterCompilationException_ShouldCarryTheInstanceFilterInvalidCode()
    {
        // The app service rethrows this instance as-is, so the code it carries is what the caller
        // sees; reclassifying it would change the public error contract.
        new FilterCompilationException("boom").Code.ShouldBe(WorkflowErrorCodes.InstanceFilterInvalid);
    }
}
