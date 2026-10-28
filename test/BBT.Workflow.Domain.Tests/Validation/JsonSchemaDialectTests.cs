using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Validation;

/// <summary>
/// A schema without <c>$schema</c> is built as Draft 2020-12. JsonSchema.Net 8 otherwise falls back to its v1
/// dialect, which refuses unknown keywords: publish accepted such a master schema, and the first write evaluating it
/// threw "Unknown keywords (x-storage) are disallowed for this dialect" (500) after the files were already stored.
/// </summary>
public sealed class JsonSchemaDialectTests
{
    private const string Vocabulary = """
        "type": "object",
        "properties": {
            "passport": { "type": "object", "x-storage": { "binding": "vnext-blob-s3" } },
            "tckn": { "type": "string", "x-roles": [ { "role": "teller", "grant": "allow" } ], "x-masking": { "type": "partial" } },
            "amount": { "type": "number", "minimum": 0 }
        },
        "required": [ "amount" ]
        """;

    public static TheoryData<IJsonSchemaValidator> Validators() =>
        [new CachedJsonSchemaValidator([], NullLogger<CachedJsonSchemaValidator>.Instance), new JsonSchemaValidator()];

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Theory]
    [MemberData(nameof(Validators))]
    public void SchemaWithoutDialect_AcceptsVocabularyKeywords(IJsonSchemaValidator validator)
        => validator.Validate(Json($$"""{ {{Vocabulary}} }"""), Json("""{ "amount": 5, "tckn": "1" }""")).IsSuccess.ShouldBeTrue();

    [Theory]
    [MemberData(nameof(Validators))]
    public void SchemaWithoutDialect_StillValidates(IJsonSchemaValidator validator)
        => validator.Validate(Json($$"""{ {{Vocabulary}} }"""), Json("""{ "amount": -1 }""")).IsSuccess.ShouldBeFalse();

    [Theory]
    [MemberData(nameof(Validators))]
    public void ExplicitDialect_IsUnchanged(IJsonSchemaValidator validator)
        => validator.Validate(Json($$"""{ "$schema": "https://json-schema.org/draft/2020-12/schema", {{Vocabulary}} }"""),
            Json("""{ "amount": 5 }""")).IsSuccess.ShouldBeTrue();
}
