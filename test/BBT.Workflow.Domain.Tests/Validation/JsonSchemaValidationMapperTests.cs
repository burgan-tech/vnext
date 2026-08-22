using System.Linq;
using System.Text.Json;
using Xunit;

namespace BBT.Workflow.Validation;

/// <summary>
/// Error-detail fidelity: a rejected payload must say WHICH field failed and why.
/// <para>
/// The hierarchical evaluation tree puts a keyword's error on the node that owns the keyword.
/// A root-level <c>required</c> failure therefore lives on the ROOT node — while that same root
/// node also has child <c>Details</c> as soon as the schema evaluates any subschema
/// (<c>properties</c>, <c>additionalProperties</c>, a nested object). Flattening that tree by
/// recursing into children and dropping the node's own errors loses the only error there was,
/// and the caller gets a 400 that names nothing.
/// </para>
/// </summary>
public class JsonSchemaValidationMapperTests : DomainTestBase<DomainEntryPoint>
{
    private readonly IJsonSchemaValidator _validator;

    public JsonSchemaValidationMapperTests()
    {
        _validator = GetRequiredService<IJsonSchemaValidator>();
    }

    /// <summary>
    /// The shape that exposed this in production: <c>additionalProperties: false</c> plus a
    /// nested object, so the root has child details AND its own required error.
    /// </summary>
    private const string PayloadSchema = """
    {
      "$schema": "https://json-schema.org/draft/2020-12/schema",
      "type": "object",
      "required": ["session", "customer"],
      "properties": {
        "session": { "type": "string" },
        "customer": {
          "type": "object",
          "required": ["ownerUserId"],
          "properties": { "ownerUserId": { "type": "string", "minLength": 1 } },
          "additionalProperties": false
        }
      },
      "additionalProperties": false
    }
    """;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static readonly SchemaValidationOptions WithDetails =
        new(Culture: "en-US", IncludeVocabularyDetails: true, CustomValidationEnabled: true);

    private static readonly SchemaValidationOptions WithoutDetails =
        new(Culture: "en-US", IncludeVocabularyDetails: false, CustomValidationEnabled: true);

    public static TheoryData<string, SchemaValidationOptions> BothDetailModes() => new()
    {
        { "vocabulary details", WithDetails },
        { "plain", WithoutDetails },
    };

    [Theory]
    [MemberData(nameof(BothDetailModes))]
    public void RootLevelRequiredFailure_NamesTheMissingProperty(string mode, SchemaValidationOptions options)
    {
        // 'customer' is missing. The root node owns that error and also has child details.
        var result = _validator.Validate(Parse(PayloadSchema), Parse("""{"session":"-"}"""), options);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error.ValidationErrors);
        Assert.True(result.Error.ValidationErrors!.Count > 0,
            $"[{mode}] the payload was rejected but no field-level error was reported at all");

        var messages = string.Join(" | ", result.Error.ValidationErrors!.Select(e => e.ErrorMessage));
        Assert.Contains("customer", messages, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(BothDetailModes))]
    public void NestedRequiredFailure_NamesTheNestedPath(string mode, SchemaValidationOptions options)
    {
        var result = _validator.Validate(
            Parse(PayloadSchema), Parse("""{"session":"-","customer":{}}"""), options);

        Assert.False(result.IsSuccess);
        var members = result.Error.ValidationErrors!.SelectMany(e => e.MemberNames).ToList();
        Assert.True(members.Contains("customer"),
            $"[{mode}] expected the nested path 'customer', got: {string.Join(", ", members)}");
    }

    [Theory]
    [MemberData(nameof(BothDetailModes))]
    public void RootAndChildFailingTogether_ReportsBoth(string mode, SchemaValidationOptions options)
    {
        // 'customer' missing (root's own error) AND 'session' of the wrong type (a child error).
        // Reporting only one of the two hides half of what the caller must fix.
        var result = _validator.Validate(
            Parse(PayloadSchema), Parse("""{"session":123}"""), options);

        Assert.False(result.IsSuccess);
        var messages = string.Join(" | ", result.Error.ValidationErrors!.Select(e => e.ErrorMessage));

        Assert.Contains("customer", messages, System.StringComparison.OrdinalIgnoreCase);
        Assert.True(
            messages.Contains("string", System.StringComparison.OrdinalIgnoreCase),
            $"[{mode}] the child type error was dropped: {messages}");
    }

    [Theory]
    [MemberData(nameof(BothDetailModes))]
    public void AdditionalPropertyFailure_NamesTheOffendingProperty(string mode, SchemaValidationOptions options)
    {
        var result = _validator.Validate(
            Parse(PayloadSchema),
            Parse("""{"session":"-","customer":{"ownerUserId":"1"},"rogue":1}"""),
            options);

        Assert.False(result.IsSuccess);
        var members = result.Error.ValidationErrors!.SelectMany(e => e.MemberNames).ToList();
        Assert.True(members.Contains("rogue"),
            $"[{mode}] expected the offending property 'rogue', got: {string.Join(", ", members)}");
    }

    [Theory]
    [MemberData(nameof(BothDetailModes))]
    public void EveryReportedError_CarriesAMemberPath(string mode, SchemaValidationOptions options)
    {
        // A keyword name ('required') is not a member path. The member must be addressable by the
        // caller so a client can attach the message to the field that produced it.
        var result = _validator.Validate(Parse(PayloadSchema), Parse("""{"session":"-"}"""), options);

        foreach (var error in result.Error.ValidationErrors!)
        {
            Assert.True(error.MemberNames.Any(),
                $"[{mode}] an error carried no member at all: {error.ErrorMessage}");
            Assert.DoesNotContain("required", error.MemberNames);
        }
    }

    [Fact]
    public void VocabularyDetails_AreNeverEmptyForARejectedPayload()
    {
        // This is what reached the client as `"details":"{\"Culture\":\"en-US\",\"Errors\":[]}"`.
        var result = _validator.Validate(Parse(PayloadSchema), Parse("""{"session":"-"}"""), WithDetails);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error.Detail);

        var details = JsonDocument.Parse(result.Error.Detail!).RootElement;
        Assert.True(details.GetProperty("Errors").GetArrayLength() > 0,
            $"the serialized vocabulary details carried no errors: {result.Error.Detail}");
    }
}
