using System;
using System.Security;
using BBT.Workflow.Security;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions.Security;

/// <summary>
/// Tests to verify SQL injection protection
/// </summary>
public class SqlInjectionTests
{
    private readonly SyncSchemaValidator _validator;

    public SqlInjectionTests()
    {
        _validator = new SyncSchemaValidator();
    }

    [Theory]
    [InlineData("public\"; DROP TABLE Instances; --")]
    [InlineData("sys_flows' OR '1'='1")]
    [InlineData("test'; DELETE FROM Instances; --")]
    [InlineData("schema\"; INSERT INTO Users VALUES ('admin'); --")]
    [InlineData("public\" UNION SELECT * FROM Users--")]
    public void SchemaValidator_SqlInjectionAttempts_ShouldBeBlocked(string maliciousSchema)
    {
        // Act & Assert
        Should.Throw<SecurityException>(() => _validator.ValidateSchemaSync(maliciousSchema));
    }

    [Theory]
    [InlineData("Instances'; DROP TABLE Users; --")]
    [InlineData("Tasks\" OR \"1\"=\"1")]
    [InlineData("Workflows; DELETE FROM Instances; --")]
    public void TableValidator_SqlInjectionAttempts_ShouldBeBlocked(string maliciousTable)
    {
        // Act & Assert
        Should.Throw<SecurityException>(() => _validator.ValidateTableName(maliciousTable));
    }

    [Theory]
    [InlineData("field'; DROP TABLE--")]
    [InlineData("../../../etc/passwd")]
    [InlineData("field\"; SELECT * FROM Users; --")]
    [InlineData("field' UNION SELECT password FROM Users--")]
    public void FieldNameValidator_SqlInjectionAttempts_ShouldBeBlocked(string maliciousField)
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => InputValidator.ValidateFieldName(maliciousField));
    }

    [Theory]
    [InlineData("SYS_FLOWS")] // Uppercase
    [InlineData("Sys_Flows")] // Mixed case
    [InlineData("sys-flows")] // Hyphen instead of underscore
    [InlineData("sys flows")] // Space
    [InlineData("sys_flows;")]
    [InlineData("sys_flows--")]
    [InlineData("sys_flows/*")]
    [InlineData("sys_flows*/")]
    public void SchemaValidator_InvalidFormats_ShouldBeBlocked(string invalidSchema)
    {
        // Act & Assert
        Should.Throw<SecurityException>(() => _validator.ValidateSchemaSync(invalidSchema));
    }

    [Fact]
    public void SchemaValidator_PathTraversal_ShouldBeBlocked()
    {
        // Arrange
        var pathTraversalAttempts = new[]
        {
            "../../../etc/passwd",
            "..\\..\\..\\windows\\system32",
            "./../schema",
            "schema/../../../etc"
        };

        // Act & Assert
        foreach (var attempt in pathTraversalAttempts)
        {
            Should.Throw<SecurityException>(() => _validator.ValidateSchemaSync(attempt));
        }
    }

    [Fact]
    public void SchemaValidator_SpecialCharacters_ShouldBeBlocked()
    {
        // Arrange - Only test characters that would actually appear in SQL injection attempts
        var specialCharAttempts = new[]
        {
            "schema;",        // SQL statement terminator
            "schema--",       // SQL comment
            "schema/*",       // SQL comment start
            "schema*/",       // SQL comment end
            "schema'",        // SQL string delimiter
            "schema\"",       // SQL string delimiter
            "schema\\",       // Escape character
            "schema/",        // Path separator
            "schema<",        // Comparison operator
            "schema>",        // Comparison operator
            "schema|",        // Pipe operator
            "schema ",        // Space (not allowed in schema names)
            "schema-name",    // Hyphen (should use underscore)
            "1schema",        // Must start with letter
            "SCHEMA",         // Uppercase (regex requires lowercase)
        };

        // Act & Assert
        foreach (var attempt in specialCharAttempts)
        {
            Should.Throw<SecurityException>(() => _validator.ValidateSchemaSync(attempt), 
                $"Expected SecurityException for: {attempt}");
        }
    }
}

