using System;
using BBT.Workflow.Security;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions.Security;

/// <summary>
/// Tests to verify JSON injection protection
/// </summary>
public class JsonInjectionTests
{
    [Theory]
    [InlineData("test\"}},\"admin\":true}//")]
    [InlineData("value\",\"isAdmin\":true,\"x\":\"")]
    [InlineData("test\\\"}}//")]
    [InlineData("value\"}]}//")]
    public void FieldValue_JsonInjectionAttempts_ShouldBeValidated(string maliciousValue)
    {
        // These values should be properly escaped by JsonSerializer
        // This test documents expected behavior
        
        // Act & Assert
        Should.NotThrow(() => InputValidator.ValidateValue(maliciousValue));
        // The actual escaping happens in BuildNestedJsonContainmentPattern
        // which now uses JsonSerializer for proper escaping
    }

    [Fact]
    public void FieldName_JsonInjectionAttempts_ShouldBeBlocked()
    {
        // Arrange
        var maliciousFieldNames = new[]
        {
            "field\"}},\"admin\":true,\"x\":\"",
            "field\\\"}}//",
            "field\",\"isAdmin\":true,\"",
            "field'}]}//",
            "field`}//",
            "field${admin}",
            "field{{admin}}"
        };

        // Act & Assert
        foreach (var fieldName in maliciousFieldNames)
        {
            Should.Throw<ArgumentException>(() => InputValidator.ValidateFieldName(fieldName));
        }
    }

    [Theory]
    [InlineData("{\"test\":\"value\"}")]
    [InlineData("{\"nested\":{\"field\":\"value\"}}")]
    [InlineData("{\"array\":[1,2,3]}")]
    public void ValidJson_ShouldPass(string validJson)
    {
        // Act & Assert
        Should.NotThrow(() => InputValidator.ValidateJsonLength(validJson));
    }

    [Fact]
    public void ExtremelyLongJson_ShouldBeRejected()
    {
        // Arrange
        var longJson = "{\"field\":\"" + new string('a', InputValidator.MaxFilterLength) + "\"}";

        // Act & Assert
        Should.Throw<ArgumentException>(() => InputValidator.ValidateJsonLength(longJson));
    }

    [Theory]
    [InlineData("field.parent.child")]
    [InlineData("a.b.c.d.e")]
    public void ValidNestedFieldNames_ShouldPass(string fieldName)
    {
        // Act & Assert
        Should.NotThrow(() => InputValidator.ValidateFieldName(fieldName));
    }

    [Fact]
    public void ExtremelyDeepNesting_ShouldBeRejected()
    {
        // Arrange - Create a field path deeper than allowed
        var parts = new string[InputValidator.MaxFieldDepth + 1];
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = $"level{i}";
        }
        var deepField = string.Join(".", parts);

        // Act & Assert
        Should.Throw<ArgumentException>(() => InputValidator.ValidateFieldName(deepField));
    }
}

