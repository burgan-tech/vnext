using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
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
}