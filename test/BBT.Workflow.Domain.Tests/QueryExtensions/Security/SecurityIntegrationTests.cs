using System;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using BBT.Workflow.Security;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions.Security;

/// <summary>
/// Integration tests for security features across the QueryExtensions module
/// </summary>
public class SecurityIntegrationTests
{
    [Fact]
    public void EndToEnd_SqlInjectionAttempt_ShouldBeBlockedAtMultipleLayers()
    {
        // Arrange
        var validator = new SyncSchemaValidator();
        var maliciousSchema = "public\"; DROP TABLE Instances; --";
        var maliciousTable = "Instances'; DELETE FROM Users; --";
        var maliciousField = "field'; DROP TABLE--";

        // Act & Assert - Each layer should block the attack
        Should.Throw<SecurityException>(() => validator.ValidateSchemaSync(maliciousSchema));
        Should.Throw<SecurityException>(() => validator.ValidateTableName(maliciousTable));
        Should.Throw<ArgumentException>(() => InputValidator.ValidateFieldName(maliciousField));
    }

    [Fact]
    public void EndToEnd_JsonInjectionAttempt_ShouldBeBlockedByFieldValidation()
    {
        // Arrange - Field name with JSON injection characters (quotes, braces, colons)
        var maliciousFieldName = @"field""}},""admin"":true,""x"":""";

        // Act & Assert - Should be blocked by special character check
        Should.Throw<ArgumentException>(() => InputValidator.ValidateFieldName(maliciousFieldName));
    }

    [Fact]
    public void EndToEnd_ReDoSAttempt_ShouldBeBlockedByInputLimits()
    {
        // Arrange - Extremely long input that could cause ReDoS
        var longInput = new string('a', InputValidator.MaxFilterLength + 1);
        var filters = new[] { longInput };

        // Act & Assert
        Should.Throw<ArgumentException>(() => InputValidator.ValidateFilters(filters));
    }

    [Fact]
    public void EndToEnd_ValidInput_ShouldPassAllValidations()
    {
        // Arrange
        var validator = new SyncSchemaValidator();
        var validSchema = "sys_flows";
        var validTable = "Instances";
        var validField = "parent.child.field";
        var validFilters = new[] { "field=eq:value", "status=eq:A" };

        // Act & Assert - All should pass
        Should.NotThrow(() => validator.ValidateSchemaSync(validSchema));
        Should.NotThrow(() => validator.ValidateTableName(validTable));
        Should.NotThrow(() => InputValidator.ValidateFieldName(validField));
        Should.NotThrow(() => InputValidator.ValidateFilters(validFilters));
    }

    [Theory]
    [InlineData("public")]
    [InlineData("sys_flows")]
    [InlineData("parent_flow")]
    public void EndToEnd_SystemSchemas_ShouldAlwaysBeValid(string schema)
    {
        // Arrange
        var validator = new SyncSchemaValidator();

        // Act
        var result = validator.ValidateSchemaSync(schema);

        // Assert
        result.ShouldBe(schema);
    }

    [Fact]
    public void EndToEnd_MultipleSecurityLayers_ShouldWorkTogether()
    {
        // This test verifies that all security layers work together correctly
        
        // Layer 1: Input validation
        var filters = new[] { "field=eq:value" };
        Should.NotThrow(() => InputValidator.ValidateFilters(filters));

        // Layer 2: Field name validation
        Should.NotThrow(() => InputValidator.ValidateFieldName("field"));

        // Layer 3: Schema validation
        var validator = new SyncSchemaValidator();
        Should.NotThrow(() => validator.ValidateSchemaSync("public"));

        // Layer 4: Table validation
        Should.NotThrow(() => validator.ValidateTableName("Instances"));

        // All layers passed - this represents a valid, safe query
    }

    [Fact]
    public void EndToEnd_AttackVectors_DocumentedAndBlocked()
    {
        // This test documents all known attack vectors and verifies they're blocked
        
        var validator = new SyncSchemaValidator();

        // Attack Vector 1: SQL Injection via schema
        Should.Throw<SecurityException>(() => 
            validator.ValidateSchemaSync("public\"; DROP TABLE Instances; --"));

        // Attack Vector 2: SQL Injection via table name
        Should.Throw<SecurityException>(() => 
            validator.ValidateTableName("Instances'; DELETE FROM Users; --"));

        // Attack Vector 3: Path traversal
        Should.Throw<SecurityException>(() => 
            validator.ValidateSchemaSync("../../../etc/passwd"));

        // Attack Vector 4: JSON injection via field name (contains quotes and braces)
        Should.Throw<ArgumentException>(() => 
            InputValidator.ValidateFieldName(@"field""}},""admin"":true,""x"":"""));

        // Attack Vector 5: ReDoS via long input
        var longInput = new string('a', InputValidator.MaxFilterLength + 1);
        Should.Throw<ArgumentException>(() => 
            InputValidator.ValidateFilters(new[] { longInput }));

        // Attack Vector 6: Deep nesting attack
        var deepField = string.Join(".", new string[InputValidator.MaxFieldDepth + 1]);
        Should.Throw<ArgumentException>(() => 
            InputValidator.ValidateFieldName(deepField));

        // Attack Vector 7: Special characters in schema
        Should.Throw<SecurityException>(() => 
            validator.ValidateSchemaSync("schema; DROP TABLE--"));

        // Attack Vector 8: Case manipulation (uppercase not allowed)
        Should.Throw<SecurityException>(() => 
            validator.ValidateSchemaSync("SYS_FLOWS"));

        // Attack Vector 9: Invalid characters (hyphen not allowed, must be underscore)
        Should.Throw<SecurityException>(() => 
            validator.ValidateSchemaSync("sys-flows"));

        // Attack Vector 10: Too many filters (DoS)
        var manyFilters = new string[InputValidator.MaxFiltersCount + 1];
        for (int i = 0; i < manyFilters.Length; i++)
            manyFilters[i] = "test=eq:value";
        Should.Throw<ArgumentException>(() => 
            InputValidator.ValidateFilters(manyFilters));
    }
}

