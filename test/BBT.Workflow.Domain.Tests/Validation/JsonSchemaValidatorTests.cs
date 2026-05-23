using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BBT.Workflow.Validation;

public class JsonSchemaValidatorTests: DomainTestBase<DomainEntryPoint>
{
    private readonly IJsonSchemaValidator _validator;

    public JsonSchemaValidatorTests()
    {
        _validator = GetRequiredService<IJsonSchemaValidator>();
    }

    [Fact]
    public void Validate_ValidData_ReturnsSuccess()
    {
        // Arrange
        var schemaJson = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                name = new { type = "string" },
                age = new { type = "integer" }
            },
            required = new[] { "name", "age" }
        });

        var dataJson = JsonSerializer.Serialize(new
        {
            name = "John",
            age = 30
        });

        var schema = JsonDocument.Parse(schemaJson).RootElement;
        var data = JsonDocument.Parse(dataJson).RootElement;

        // Act
        var result = _validator.Validate(schema, data);

        // Assert
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Validate_InvalidData_ReturnsFailureWithValidationErrors()
    {
        // Arrange
        var schemaJson = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                name = new { type = "string" },
                age = new { type = "integer" }
            },
            required = new[] { "name", "age" }
        });

        var dataJson = JsonSerializer.Serialize(new
        {
            name = "John"
            // age missing
        });

        var schema = JsonDocument.Parse(schemaJson).RootElement;
        var data = JsonDocument.Parse(dataJson).RootElement;

        // Act
        var result = _validator.Validate(schema, data);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkflowErrorCodes.ValidationErrors, result.Error.Code);
        Assert.NotNull(result.Error.ValidationErrors);
        Assert.NotEmpty(result.Error.ValidationErrors);
        Assert.Contains(result.Error.ValidationErrors, vr => vr.MemberNames.Contains("required") || vr.MemberNames.Contains("age"));
    }

    [Fact]
    public void Validate_NullDataWithRequiredSchema_ReturnsFailureWithValidationErrors()
    {
        // Arrange
        var schemaJson = JsonSerializer.Serialize(new
        {
            type = "object",
            properties = new
            {
                id = new { type = "string" }
            },
            required = new[] { "id" }
        });

        var schema = JsonDocument.Parse(schemaJson).RootElement;

        // Act
        var result = _validator.Validate(schema, null);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(WorkflowErrorCodes.ValidationErrors, result.Error.Code);
        Assert.NotNull(result.Error.ValidationErrors);
        Assert.NotEmpty(result.Error.ValidationErrors);
    }

    [Fact]
    public void Validate_SameSchemaMultipleTimes_UsesCachedInstance()
    {
        // Arrange
        var schemaJson = """
            {
                "$id": "urn:test-schema",
                "type": "object",
                "properties": { "name": { "type": "string" } }
            }
            """;

        var schema = JsonDocument.Parse(schemaJson).RootElement;
        var data = JsonDocument.Parse("{\"name\":\"test\"}").RootElement;

        // Act - First validation builds and caches
        var result1 = _validator.Validate(schema, data);

        // Act - Second validation uses cached instance
        var result2 = _validator.Validate(schema, data);

        // Assert
        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);
    }

    [Fact]
    public void Validate_DifferentSchemasWithSameId_BothWorkCorrectly()
    {
        // Arrange - Two different schemas with same $id (simulating multiple versions)
        var schema1Json = """
            {
                "$id": "urn:same-id",
                "type": "object",
                "required": ["field1"]
            }
            """;

        var schema2Json = """
            {
                "$id": "urn:same-id",
                "type": "object",
                "required": ["field2"]
            }
            """;

        var schema1 = JsonDocument.Parse(schema1Json).RootElement;
        var schema2 = JsonDocument.Parse(schema2Json).RootElement;

        var data1 = JsonDocument.Parse("{\"field1\":\"value\"}").RootElement;
        var data2 = JsonDocument.Parse("{\"field2\":\"value\"}").RootElement;

        // Act - Both should validate correctly with their respective data
        var result1 = _validator.Validate(schema1, data1);
        var result2 = _validator.Validate(schema2, data2);

        // Assert
        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);
    }

    [Fact]
    public void Validate_MultipleSchemaVersions_ValidateIndependently()
    {
        // Arrange - Multiple versions of a schema with same $id but different validation rules
        var schemaV1Json = """
            {
                "$id": "urn:myschema",
                "type": "object",
                "properties": {
                    "name": { "type": "string", "minLength": 3 }
                },
                "required": ["name"]
            }
            """;

        var schemaV2Json = """
            {
                "$id": "urn:myschema",
                "type": "object",
                "properties": {
                    "name": { "type": "string", "minLength": 5 }
                },
                "required": ["name"]
            }
            """;

        var schemaV1 = JsonDocument.Parse(schemaV1Json).RootElement;
        var schemaV2 = JsonDocument.Parse(schemaV2Json).RootElement;

        var dataShortName = JsonDocument.Parse("{\"name\":\"abc\"}").RootElement; // 3 chars
        var dataLongName = JsonDocument.Parse("{\"name\":\"abcdef\"}").RootElement; // 6 chars

        // Act
        var v1WithShort = _validator.Validate(schemaV1, dataShortName); // Should pass (minLength=3)
        var v1WithLong = _validator.Validate(schemaV1, dataLongName); // Should pass (minLength=3)
        var v2WithShort = _validator.Validate(schemaV2, dataShortName); // Should fail (minLength=5)
        var v2WithLong = _validator.Validate(schemaV2, dataLongName); // Should pass (minLength=5)

        // Assert - Each version validates independently
        Assert.True(v1WithShort.IsSuccess, "V1 should accept 3-char name");
        Assert.True(v1WithLong.IsSuccess, "V1 should accept 6-char name");
        Assert.False(v2WithShort.IsSuccess, "V2 should reject 3-char name");
        Assert.True(v2WithLong.IsSuccess, "V2 should accept 6-char name");
    }

    [Fact]
    public async Task Validate_ConcurrentValidation_IsThreadSafe()
    {
        // Arrange
        var schemaJson = """
            {
                "$id": "urn:concurrent-test",
                "type": "object",
                "properties": {
                    "value": { "type": "integer", "minimum": 0 }
                }
            }
            """;

        var schema = JsonDocument.Parse(schemaJson).RootElement;

        // Act - Run multiple validations concurrently
        var tasks = Enumerable.Range(0, 100).Select(i =>
            Task.Run(() =>
            {
                var dataJson = JsonSerializer.Serialize(new { value = i });
                var data = JsonDocument.Parse(dataJson).RootElement;
                return _validator.Validate(schema, data);
            })
        ).ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert - All validations should succeed
        Assert.All(results, result => Assert.True(result.IsSuccess));
    }

    [Fact]
    public void Validate_DifferentSchemaContent_UseDifferentCacheEntries()
    {
        // Arrange - Two schemas with same $id but different content
        var schema1Json = """
            {
                "$id": "urn:cached-schema",
                "type": "object",
                "properties": { "field1": { "type": "string" } }
            }
            """;

        var schema2Json = """
            {
                "$id": "urn:cached-schema",
                "type": "object",
                "properties": { "field2": { "type": "number" } }
            }
            """;

        var schema1 = JsonDocument.Parse(schema1Json).RootElement;
        var schema2 = JsonDocument.Parse(schema2Json).RootElement;

        var data1 = JsonDocument.Parse("{\"field1\":\"text\"}").RootElement;
        var data2 = JsonDocument.Parse("{\"field2\":42}").RootElement;

        // Act
        var result1 = _validator.Validate(schema1, data1);
        var result2 = _validator.Validate(schema2, data2);

        // Assert - Both should succeed with their respective schemas
        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);
    }

    [Fact]
    public void Validate_SchemaWithComplexId_HandlesCorrectly()
    {
        // Arrange - Schema with complex URN including version info
        var schemaJson = """
            {
                "$id": "urn:company:api:user:v1.0.0",
                "type": "object",
                "properties": {
                    "username": { "type": "string", "pattern": "^[a-z]+$" }
                },
                "required": ["username"]
            }
            """;

        var schema = JsonDocument.Parse(schemaJson).RootElement;
        var validData = JsonDocument.Parse("{\"username\":\"john\"}").RootElement;
        var invalidData = JsonDocument.Parse("{\"username\":\"John123\"}").RootElement;

        // Act
        var validResult = _validator.Validate(schema, validData);
        var invalidResult = _validator.Validate(schema, invalidData);

        // Assert
        Assert.True(validResult.IsSuccess);
        Assert.False(invalidResult.IsSuccess);
    }

    [Fact]
    public void Validate_WithLocalizedVocabularyDetails_UsesRequestedCulture()
    {
        // Arrange
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "customer": {
                        "type": "object",
                        "properties": {
                            "identityNumber": {
                                "type": "string",
                                "minLength": 11,
                                "x-labels": {
                                    "tr-TR": "TCKN",
                                    "en-US": "Identity number"
                                },
                                "x-errorMessages": {
                                    "minLength": {
                                        "tr-TR": "TCKN 11 karakter olmalıdır.",
                                        "en-US": "Identity number must be 11 characters."
                                    }
                                }
                            }
                        },
                        "required": ["identityNumber"]
                    }
                }
            }
            """).RootElement;
        var data = JsonDocument.Parse("""
            {
                "customer": {
                    "identityNumber": "123"
                }
            }
            """).RootElement;

        // Act
        var result = _validator.Validate(
            schema,
            data,
            new SchemaValidationOptions(Culture: "tr-TR", IncludeVocabularyDetails: true));

        // Assert
        Assert.False(result.IsSuccess);
        var validationError = Assert.Single(result.Error.ValidationErrors!);
        Assert.Equal("TCKN 11 karakter olmalıdır.", validationError.ErrorMessage);
        Assert.Contains("customer.identityNumber", validationError.MemberNames);

        var details = JsonSerializer.Deserialize<SchemaValidationProblemDetails>(result.Error.Detail!);
        Assert.NotNull(details);
        Assert.Equal("tr-TR", details!.Culture);
        var error = Assert.Single(details.Errors);
        Assert.Equal("customer.identityNumber", error.Path);
        Assert.Equal("minLength", error.Keyword);
        Assert.Equal("schema.minLength", error.Code);
        Assert.Equal("TCKN", error.Label);
        Assert.Equal("TCKN 11 karakter olmalıdır.", error.Message);
        Assert.Equal(11, error.Parameters["minLength"].GetInt32());
    }

    [Fact]
    public void Validate_WithUnsupportedCulture_FallsBackToEnglishVocabulary()
    {
        // Arrange
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": {
                        "type": "string",
                        "minLength": 5,
                        "x-labels": {
                            "tr-TR": "Ad",
                            "en-US": "Name"
                        },
                        "x-errorMessages": {
                            "minLength": {
                                "tr-TR": "Ad en az 5 karakter olmalıdır.",
                                "en-US": "Name must be at least 5 characters."
                            }
                        }
                    }
                }
            }
            """).RootElement;
        var data = JsonDocument.Parse("""{"name":"abc"}""").RootElement;

        // Act
        var result = _validator.Validate(
            schema,
            data,
            new SchemaValidationOptions(Culture: "de-DE", IncludeVocabularyDetails: true));

        // Assert
        Assert.False(result.IsSuccess);
        var validationError = Assert.Single(result.Error.ValidationErrors!);
        Assert.Equal("Name must be at least 5 characters.", validationError.ErrorMessage);

        var details = JsonSerializer.Deserialize<SchemaValidationProblemDetails>(result.Error.Detail!);
        Assert.NotNull(details);
        Assert.Equal("de-DE", details!.Culture);
        Assert.Equal("Name", Assert.Single(details.Errors).Label);
    }

    [Fact]
    public void Validate_WithBusinessPropertyNamedLabels_DoesNotStripPropertySchema()
    {
        // Arrange
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "labels": {
                        "type": "string",
                        "minLength": 3
                    }
                }
            }
            """).RootElement;
        var data = JsonDocument.Parse("""{"labels":"ab"}""").RootElement;

        // Act
        var result = _validator.Validate(
            schema,
            data,
            new SchemaValidationOptions(Culture: "en-US", IncludeVocabularyDetails: true));

        // Assert
        Assert.False(result.IsSuccess);
        var details = JsonSerializer.Deserialize<SchemaValidationProblemDetails>(result.Error.Detail!);
        var error = Assert.Single(details!.Errors);
        Assert.Equal("labels", error.Path);
        Assert.Equal("minLength", error.Keyword);
    }

    [Fact]
    public void Validate_WithRegisteredCustomValidationRule_ReturnsLocalizedRuleError()
    {
        // Arrange
        var validator = new CachedJsonSchemaValidator(
            [new BlockedValueSchemaValidationRule()],
            NullLogger<CachedJsonSchemaValidator>.Instance);
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "code": {
                        "type": "string",
                        "x-validation": {
                            "rule": "blockedValue",
                            "parameters": { "blocked": "BLOCKED" },
                            "errorMessages": {
                                "tr-TR": "Bu değer kullanılamaz.",
                                "en-US": "This value cannot be used."
                            }
                        }
                    }
                }
            }
            """).RootElement;
        var data = JsonDocument.Parse("""{"code":"BLOCKED"}""").RootElement;

        // Act
        var result = validator.Validate(
            schema,
            data,
            new SchemaValidationOptions(Culture: "tr-TR", IncludeVocabularyDetails: true, CustomValidationEnabled: true));

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("Bu değer kullanılamaz.", Assert.Single(result.Error.ValidationErrors!).ErrorMessage);
        var details = JsonSerializer.Deserialize<SchemaValidationProblemDetails>(result.Error.Detail!);
        var error = Assert.Single(details!.Errors);
        Assert.Equal("code", error.Path);
        Assert.Equal("x-validation", error.Keyword);
        Assert.Equal("schema.x-validation.blockedValue", error.Code);
    }

    [Fact]
    public void Validate_WithUnknownCustomValidationRule_SkipsRule()
    {
        // Arrange
        var validator = new CachedJsonSchemaValidator([], NullLogger<CachedJsonSchemaValidator>.Instance);
        var schema = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "code": {
                        "type": "string",
                        "x-validation": {
                            "rule": "missingRule",
                            "errorMessages": {
                                "en-US": "This should not be returned."
                            }
                        }
                    }
                }
            }
            """).RootElement;
        var data = JsonDocument.Parse("""{"code":"BLOCKED"}""").RootElement;

        // Act
        var result = validator.Validate(
            schema,
            data,
            new SchemaValidationOptions(Culture: "en-US", IncludeVocabularyDetails: true, CustomValidationEnabled: true));

        // Assert
        Assert.True(result.IsSuccess);
    }

    private sealed class BlockedValueSchemaValidationRule : IJsonSchemaCustomValidationRule
    {
        public string Name => "blockedValue";

        public bool IsValid(JsonElement value, JsonElement? parameters)
        {
            var blocked = parameters is { ValueKind: JsonValueKind.Object } p &&
                          p.TryGetProperty("blocked", out var blockedElement)
                ? blockedElement.GetString()
                : null;

            return value.ValueKind != JsonValueKind.String || value.GetString() != blocked;
        }
    }
}
