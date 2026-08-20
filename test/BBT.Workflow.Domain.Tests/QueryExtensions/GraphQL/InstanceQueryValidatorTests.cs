using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using BBT.Workflow.Definitions.GraphQL;
using BBT.Workflow.Definitions.GraphQL.Validation;
using BBT.Workflow.Security;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions.GraphQL;

/// <summary>
/// Unit tests for <see cref="InstanceQueryValidator"/>.
/// </summary>
/// <remarks>
/// These pin the fail-closed contract: anything the runtime cannot execute exactly as authored is
/// rejected. Before this validator existed, an unsupported operator, a malformed filter and a
/// truncated filter all ran the query unfiltered and answered HTTP 200 with every row.
/// </remarks>
public class InstanceQueryValidatorTests
{
    private static FilterValidationResult ValidateFilter(string? filter) =>
        InstanceQueryValidator.Validate(new InstanceQueryValidationRequest { Filter = filter });

    private static FilterValidationResult ValidateSort(string? sort) =>
        InstanceQueryValidator.Validate(new InstanceQueryValidationRequest { Sort = sort });

    #region Blank input

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ShouldPass_WhenEverythingIsBlank(string? value)
    {
        var result = InstanceQueryValidator.Validate(new InstanceQueryValidationRequest
        {
            Filter = value,
            Sort = value,
            GroupBy = value,
            Aggregations = value
        });

        result.IsValid.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    #endregion

    #region Unsupported operators

    [Theory]
    [InlineData("gte", "ge")]
    [InlineData("lte", "le")]
    [InlineData("neq", "ne")]
    [InlineData("contains", "like")]
    public void Validate_ShouldRejectSchemaSpelling_WithCorrectionHint(string authored, string suggestion)
    {
        var result = ValidateFilter("{\"attributes\":{\"amount\":{\"" + authored + "\":100}}}");

        result.IsValid.ShouldBeFalse();
        var error = result.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe("filter.unknownOperator");
        error.Message.ShouldContain($"'{authored}'");
        error.Message.ShouldContain($"Did you mean '{suggestion}'?");
        result.PrimaryErrorCode.ShouldBe(WorkflowErrorCodes.InstanceFilterInvalid);
    }

    [Fact]
    public void Validate_ShouldRejectUnknownOperator_AndListSupportedOnes()
    {
        var result = ValidateFilter("""{"attributes":{"amount":{"zzz":100}}}""");

        result.IsValid.ShouldBeFalse();
        var error = result.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe("filter.unknownOperator");
        error.Message.ShouldContain("'zzz'");
        error.Message.ShouldContain("eq");
        error.Message.ShouldContain("between");
    }

    [Fact]
    public void Validate_ShouldRejectSchemaSpelling_WhenValueIsAnObject()
    {
        // An object value routes through the nested-condition branch of the converter. A nested
        // field is never legitimately named after a known operator misspelling.
        var result = ValidateFilter("""{"attributes":{"amount":{"gte":{"x":1}}}}""");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "filter.unknownOperator");
    }

    [Fact]
    public void Validate_ShouldReportEveryUnknownOperator()
    {
        var result = ValidateFilter("""{"attributes":{"amount":{"gte":1},"score":{"lte":2}}}""");

        result.IsValid.ShouldBeFalse();
        result.Errors.Count.ShouldBe(2);
        result.Errors.ShouldAllBe(e => e.Code == "filter.unknownOperator");
    }

    [Fact]
    public void Validate_ShouldAllowNestedFieldPath()
    {
        // Regression guard: nested paths hit the same converter branch as an unknown operator and
        // must not be flagged.
        var result = ValidateFilter("""{"attributes":{"parent":{"child":{"eq":1}}}}""");

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldRejectEmptyOperatorName()
    {
        var result = ValidateFilter("""{"attributes":{"amount":{"":1}}}""");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "filter.emptyOperatorName");
    }

    [Fact]
    public void Validate_ShouldAcceptEverySupportedOperator()
    {
        var result = ValidateFilter("""
            {"attributes":{
              "a":{"eq":1},"b":{"ne":1},"c":{"gt":1},"d":{"ge":1},"e":{"lt":1},"f":{"le":1},
              "g":{"between":[1,2]},"h":{"like":"x"},"i":{"match":"x"},"j":{"startswith":"x"},
              "k":{"endswith":"x"},"l":{"in":[1,2]},"m":{"nin":[1,2]},"n":{"isNull":true},
              "o":{"includes":{"p":1}}
            }}
            """);

        result.IsValid.ShouldBeTrue();
    }

    /// <summary>
    /// Pins the invariant that the validator's supported-operator set matches the operator switch
    /// in <see cref="GraphQLFilterNodeConverter"/>, whose surface is <see cref="FieldCondition"/>'s
    /// JSON property names. Adding a 16th operator without updating both sides fails here.
    /// </summary>
    [Fact]
    public void SupportedOperators_ShouldMatchFieldConditionSurface()
    {
        var converterOperators = typeof(FieldCondition)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        FilterOperators.Supported.ShouldBe(converterOperators, ignoreOrder: true);
    }

    #endregion

    #region Malformed and empty filters

    [Fact]
    public void Validate_ShouldRejectTruncatedFilter_ThatNoLongerEndsWithBrace()
    {
        // DetectFormat requires a trailing '}', so this was classified as Empty: the runtime never
        // parsed it, threw nothing at all, and applied no filter.
        var result = ValidateFilter("""{"attributes":{"amount":{"eq":100""");

        result.IsValid.ShouldBeFalse();
        var error = result.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe("filter.unrecognizedFormat");
        error.Message.ShouldContain("brace");
    }

    [Theory]
    // One brace short, so it still ends with '}' and reaches the parser.
    [InlineData("""{"attributes":{"amount":{"eq":100}}""")]
    // Missing value.
    [InlineData("""{"attributes":{"amount":{"eq":}}}""")]
    public void Validate_ShouldRejectMalformedJson_ThatStillEndsWithBrace(string filter)
    {
        var result = ValidateFilter(filter);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "filter.invalidJson");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"attributes":{}}""")]
    public void Validate_ShouldAcceptEmptyFilter(string filter)
    {
        // "No filters selected" is a common client idiom and legitimately means no restriction.
        // GraphQLJsonFilterService's own empty-clause guard says the same, so rejecting it here
        // would make the two contradict each other on the same input.
        ValidateFilter(filter).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{"and":[]}""")]
    [InlineData("""{"or":[]}""")]
    public void Validate_ShouldRejectEmptyLogicalOperator(string filter)
    {
        var result = ValidateFilter(filter);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "filter.emptyLogicalOperator");
    }

    [Fact]
    public void Validate_ShouldRejectFieldWithNoOperator()
    {
        var result = ValidateFilter("""{"attributes":{"amount":{}}}""");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe("filter.noOperator");
    }

    [Fact]
    public void Validate_ShouldRejectUnknownNodeProperty()
    {
        var result = ValidateFilter("""{"attributes":{"a":{"eq":1}},"wat":5}""");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "filter.unknownProperty");
    }

    [Fact]
    public void Validate_ShouldRejectOverlongFilter_AsResultNotException()
    {
        var filter = "{\"attributes\":{\"a\":{\"eq\":\""
                     + new string('x', InputValidator.MaxFilterLength)
                     + "\"}}}";

        var result = ValidateFilter(filter);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe("filter.tooLong");
    }

    [Fact]
    public void Validate_ShouldAcceptLegacyFormat()
    {
        var result = ValidateFilter("status=eq:A");

        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{"fields":["status"]}""", null)]
    [InlineData(null, """{"count":true}""")]
    public void Validate_ShouldRejectLegacyFilter_CombinedWithAggregation(string? groupBy, string? aggregations)
    {
        // The aggregation path feeds the filter straight into the GraphQL parser without converting
        // it from legacy form, so this combination cannot execute.
        var result = InstanceQueryValidator.Validate(new InstanceQueryValidationRequest
        {
            Filter = "status=eq:A",
            GroupBy = groupBy,
            Aggregations = aggregations
        });

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "filter.legacyNotAggregatable");
    }

    [Fact]
    public void Validate_ShouldRejectLegacyFormat_WithUnsupportedOperator()
    {
        // Fails the legacy operator whitelist in DetectFormat, so it is not classified as Legacy.
        var result = ValidateFilter("status=zzz:A");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe("filter.unrecognizedFormat");
    }

    #endregion

    #region Envelope form

    [Fact]
    public void Validate_ShouldAcceptWellFormedEnvelope()
    {
        var result = ValidateFilter("""
            {"filter":{"attributes":{"amount":{"ge":100}}},
             "groupBy":{"fields":["attributes.scope"],"aggregations":{"sum":"attributes.amount"}}}
            """);

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldRejectUnknownOperator_InsideEnvelope()
    {
        var result = ValidateFilter("""
            {"filter":{"attributes":{"amount":{"gte":100}}},"groupBy":{"fields":["status"]}}
            """);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "filter.unknownOperator");
    }

    [Fact]
    public void Validate_ShouldRejectGroupByAsBareArray()
    {
        // groupBy takes an object. The array form deserialized to nothing and ran ungrouped.
        var result = ValidateFilter("""{"groupBy":["status"]}""");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "filter.invalidJson");
    }

    [Fact]
    public void Validate_ShouldRejectUnknownAggregationFunction_InsideEnvelope()
    {
        var result = ValidateFilter("""{"aggregations":{"median":"attributes.amount"}}""");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "filter.invalidJson");
    }

    [Fact]
    public void Validate_ShouldRejectMisspelledEnvelopeFilterKey()
    {
        // GraphQLFilterRequest tolerates unknown members so format sniffing keeps working, which
        // meant a typo'd "filter" key dropped every condition and aggregated over the whole table
        // with HTTP 200.
        var result = ValidateFilter("""
            {"fitler":{"attributes":{"status":{"eq":"A"}}},"groupBy":{"fields":["currentState"]}}
            """);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "filter.unknownProperty" && e.Message.Contains("fitler"));
    }

    [Fact]
    public void Validate_ShouldAcceptGroupByOnlyEnvelope()
    {
        // Grouping every instance is a legitimate request; only an *unrecognized* key is not.
        ValidateFilter("""{"groupBy":{"fields":["currentState"]}}""").IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldRejectEnvelopeCombinedWithSeparateGroupByParameter()
    {
        // The app service unwraps the envelope only when no groupBy parameter was supplied, so
        // supplying both silently discards the envelope's grouping.
        var result = InstanceQueryValidator.Validate(new InstanceQueryValidationRequest
        {
            Filter = """{"filter":{"attributes":{"a":{"eq":1}}},"aggregations":{"count":true}}""",
            GroupBy = """{"fields":["status"]}"""
        });

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "filter.ambiguousEnvelope");
    }

    #endregion

    #region Sort

    [Theory]
    [InlineData("""{"field":"createdAt","direction":"desc"}""")]
    [InlineData("""{"field":"status"}""")]
    [InlineData("""{"field":"attributes.amount","direction":"ASC"}""")]
    [InlineData("""{"fields":[{"field":"status","direction":"asc"},{"field":"createdAt","direction":"desc"}]}""")]
    public void Validate_ShouldAcceptWellFormedSort(string sort)
    {
        ValidateSort(sort).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldRejectInvalidSortJson()
    {
        var result = ValidateSort("not valid json");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe("sort.invalidJson");
        result.PrimaryErrorCode.ShouldBe(WorkflowErrorCodes.InstanceSortInvalid);
    }

    [Fact]
    public void Validate_ShouldRejectInvalidSortDirection()
    {
        // NormalizeDirection silently coerced anything non-"desc" to ascending.
        var result = ValidateSort("""{"field":"createdAt","direction":"sideways"}""");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe("sort.invalidDirection");
    }

    [Fact]
    public void Validate_ShouldRejectUnknownSortField()
    {
        var result = ValidateSort("""{"field":"nope"}""");

        result.IsValid.ShouldBeFalse();
        var error = result.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe("sort.unknownField");
        error.Message.ShouldContain("createdAt");
    }

    [Fact]
    public void Validate_ShouldRejectUnsafeAttributesSortPath()
    {
        var result = ValidateSort("""{"field":"attributes.a b"}""");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe("sort.unsafePath");
    }

    [Theory]
    [InlineData("""{"field":"","direction":"asc"}""")]
    [InlineData("""{"fields":[{"direction":"asc"}]}""")]
    public void Validate_ShouldRejectSortWithNoField(string sort)
    {
        var result = ValidateSort(sort);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Code == "sort.emptyField");
    }

    [Fact]
    public void Validate_ShouldRejectSortEntryWithNoField_AmongValidOnes()
    {
        var result = ValidateSort("""{"fields":[{"field":"status"},{"direction":"asc"}]}""");

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe("sort.emptyField");
    }

    #endregion

    #region GroupBy and aggregations

    [Fact]
    public void Validate_ShouldAcceptWellFormedGroupBy()
    {
        var result = InstanceQueryValidator.Validate(new InstanceQueryValidationRequest
        {
            GroupBy = """{"fields":["attributes.scope"]}"""
        });

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldRejectGroupByWithUnknownMember()
    {
        var result = InstanceQueryValidator.Validate(new InstanceQueryValidationRequest
        {
            GroupBy = """{"fieldz":"status"}"""
        });

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe("groupBy.invalidJson");
        result.PrimaryErrorCode.ShouldBe(WorkflowErrorCodes.InstanceGroupByInvalid);
    }

    [Fact]
    public void Validate_ShouldRejectGroupByWithNoFields()
    {
        var result = InstanceQueryValidator.Validate(new InstanceQueryValidationRequest
        {
            GroupBy = "{}"
        });

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe("groupBy.noFields");
    }

    [Fact]
    public void Validate_ShouldRejectUnknownAggregationFunction()
    {
        // AggregationRequest has no extension data, so "median" used to deserialize to an empty
        // request and the query silently degraded into a plain, unaggregated list.
        var result = InstanceQueryValidator.Validate(new InstanceQueryValidationRequest
        {
            Aggregations = """{"median":"attributes.amount"}"""
        });

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe("aggregations.invalidJson");
        result.PrimaryErrorCode.ShouldBe(WorkflowErrorCodes.InstanceAggregationInvalid);
    }

    [Fact]
    public void Validate_ShouldRejectEmptyAggregations()
    {
        var result = InstanceQueryValidator.Validate(new InstanceQueryValidationRequest
        {
            Aggregations = "{}"
        });

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe("aggregations.empty");
    }

    [Theory]
    [InlineData("""{"count":true}""")]
    [InlineData("""{"count":"attributes.id"}""")]
    [InlineData("""{"sum":"attributes.amount","avg":"attributes.score"}""")]
    public void Validate_ShouldAcceptWellFormedAggregations(string aggregations)
    {
        InstanceQueryValidator
            .Validate(new InstanceQueryValidationRequest { Aggregations = aggregations })
            .IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldRejectNumericCount()
    {
        // A numeric count produces no SQL, so the aggregation was silently dropped.
        var result = InstanceQueryValidator.Validate(new InstanceQueryValidationRequest
        {
            Aggregations = """{"count":5}"""
        });

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe("aggregations.invalidCount");
    }

    [Fact]
    public void Validate_ShouldRejectUnsafeAggregationFieldName()
    {
        var result = InstanceQueryValidator.Validate(new InstanceQueryValidationRequest
        {
            Aggregations = """{"sum":"1bad-name"}"""
        });

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe("aggregations.invalidField");
    }

    #endregion

    #region Aggregate behavior

    [Fact]
    public void Validate_ShouldCollectErrorsAcrossParameters()
    {
        var result = InstanceQueryValidator.Validate(new InstanceQueryValidationRequest
        {
            Filter = """{"attributes":{"amount":{"gte":1}}}""",
            Sort = """{"field":"nope"}""",
            Aggregations = "{}"
        });

        result.IsValid.ShouldBeFalse();
        result.Errors.Select(e => e.Code).ShouldBe(
            ["filter.unknownOperator", "sort.unknownField", "aggregations.empty"],
            ignoreOrder: true);

        // The filter error came first, so it drives the top-level code.
        result.PrimaryErrorCode.ShouldBe(WorkflowErrorCodes.InstanceFilterInvalid);
    }

    [Fact]
    public void Validate_ShouldCapErrorCount()
    {
        var fields = string.Join(",", Enumerable.Range(0, 40).Select(i => "\"f" + i + "\":{\"gte\":1}"));

        var result = ValidateFilter("{\"attributes\":{" + fields + "}}");

        result.IsValid.ShouldBeFalse();
        result.Errors.Count.ShouldBe(FilterValidationResult.MaxErrors);
    }

    [Fact]
    public void Validate_ShouldThrow_WhenRequestIsNull()
    {
        Should.Throw<ArgumentNullException>(() => InstanceQueryValidator.Validate(null!));
    }

    #endregion
}
