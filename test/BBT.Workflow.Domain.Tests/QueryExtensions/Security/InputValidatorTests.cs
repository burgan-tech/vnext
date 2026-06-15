using System;
using System.Linq;
using System.Text.Json;
using BBT.Workflow.Security;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions.Security;

public class InputValidatorTests
{
    [Fact]
    public void ValidateFilters_NullOrEmpty_ShouldPass()
    {
        // Act & Assert - test both overloads
        Should.NotThrow(() => InputValidator.ValidateFilters((string?)null));
        Should.NotThrow(() => InputValidator.ValidateFilters((string[]?)null));
        Should.NotThrow(() => InputValidator.ValidateFilters(Array.Empty<string>()));
    }

    [Fact]
    public void ValidateFilters_TooManyFilters_ShouldThrow()
    {
        // Arrange
        var filters = new string[InputValidator.MaxFiltersCount + 1];
        for (int i = 0; i < filters.Length; i++)
        {
            filters[i] = "test=eq:value";
        }

        // Act & Assert
        Should.Throw<ArgumentException>(() => InputValidator.ValidateFilters(filters));
    }

    [Fact]
    public void ValidateFilters_FilterTooLong_ShouldThrow()
    {
        // Arrange
        var longFilter = new string('a', InputValidator.MaxFilterLength + 1);
        var filters = new[] { longFilter };

        // Act & Assert
        Should.Throw<ArgumentException>(() => InputValidator.ValidateFilters(filters));
    }

    [Fact]
    public void ValidateFieldName_ValidFieldName_ShouldPass()
    {
        // Act & Assert
        Should.NotThrow(() => InputValidator.ValidateFieldName("fieldName"));
        Should.NotThrow(() => InputValidator.ValidateFieldName("parent.child"));
        Should.NotThrow(() => InputValidator.ValidateFieldName("a.b.c"));
    }

    [Fact]
    public void ValidateFieldName_TooLong_ShouldThrow()
    {
        // Arrange
        var longFieldName = new string('a', InputValidator.MaxFieldNameLength + 1);

        // Act & Assert
        Should.Throw<ArgumentException>(() => InputValidator.ValidateFieldName(longFieldName));
    }

    [Fact]
    public void ValidateFieldName_TooDeep_ShouldThrow()
    {
        // Arrange
        var deepFieldName = string.Join(".", new string[InputValidator.MaxFieldDepth + 1]);

        // Act & Assert
        Should.Throw<ArgumentException>(() => InputValidator.ValidateFieldName(deepFieldName));
    }

    [Fact]
    public void ValidateFieldName_EmptyPart_ShouldThrow()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => InputValidator.ValidateFieldName("parent..child"));
    }

    [Fact]
    public void ValidateValue_ValidValue_ShouldPass()
    {
        // Act & Assert
        Should.NotThrow(() => InputValidator.ValidateValue("test"));
        Should.NotThrow(() => InputValidator.ValidateValue(null));
    }

    [Fact]
    public void ValidateValue_TooLong_ShouldThrow()
    {
        // Arrange
        var longValue = new string('a', InputValidator.MaxValueLength + 1);

        // Act & Assert
        Should.Throw<ArgumentException>(() => InputValidator.ValidateValue(longValue));
    }

    [Fact]
    public void ValidateJsonLength_ValidJson_ShouldPass()
    {
        // Act & Assert
        Should.NotThrow(() => InputValidator.ValidateJsonLength("{\"test\":\"value\"}"));
        Should.NotThrow(() => InputValidator.ValidateJsonLength(null));
    }

    [Fact]
    public void ValidateJsonLength_TooLong_ShouldThrow()
    {
        // Arrange
        var longJson = new string('a', InputValidator.MaxFilterLength + 1);

        // Act & Assert
        Should.Throw<ArgumentException>(() => InputValidator.ValidateJsonLength(longJson));
    }

    [Fact]
    public void ValidateSqlJsonColumnIdentifier_ValidNames_ShouldPass()
    {
        Should.NotThrow(() => InputValidator.ValidateSqlJsonColumnIdentifier("Data"));
        Should.NotThrow(() => InputValidator.ValidateSqlJsonColumnIdentifier("Json"));
        Should.NotThrow(() => InputValidator.ValidateSqlJsonColumnIdentifier("_v1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("9bad")]
    [InlineData("Da-ta")]
    [InlineData("Data\"; DROP--")]
    public void ValidateSqlJsonColumnIdentifier_Invalid_ShouldThrow(string name)
    {
        Should.Throw<ArgumentException>(() => InputValidator.ValidateSqlJsonColumnIdentifier(name));
    }

    [Fact]
    public void EscapePostgresSingleQuotedString_DoublesQuotes()
    {
        InputValidator.EscapePostgresSingleQuotedString("a'b").ShouldBe("a''b");
        InputValidator.EscapePostgresSingleQuotedString("ok").ShouldBe("ok");
    }

    [Fact]
    public void ValidateIncludesObject_ValidObject_ShouldPass()
    {
        using var doc = JsonDocument.Parse("""{"memberId":"ia002","role":"advisor"}""");
        Should.NotThrow(() => InputValidator.ValidateIncludesObject(doc.RootElement));
    }

    [Fact]
    public void ValidateIncludesObject_ArrayRoot_ShouldThrow()
    {
        using var doc = JsonDocument.Parse("""[1,2]""");
        Should.Throw<ArgumentException>(() => InputValidator.ValidateIncludesObject(doc.RootElement));
    }

    [Fact]
    public void ValidateIncludesObject_TooManyTopLevelProperties_ShouldThrow()
    {
        var props = Enumerable.Range(1, InputValidator.MaxIncludesPayloadPropertyCount + 1)
            .Select(i => $"\"k{i}\":1");
        var json = "{" + string.Join(",", props) + "}";
        using var doc = JsonDocument.Parse(json);
        Should.Throw<ArgumentException>(() => InputValidator.ValidateIncludesObject(doc.RootElement));
    }

    [Fact]
    public void ValidateIncludesObject_NestingTooDeep_ShouldThrow()
    {
        // Exceed MaxIncludesPayloadNestingDepth nested objects
        var depth = InputValidator.MaxIncludesPayloadNestingDepth + 2;
        var json = string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "1" + string.Concat(Enumerable.Repeat("}", depth));
        using var doc = JsonDocument.Parse(json);
        Should.Throw<ArgumentException>(() => InputValidator.ValidateIncludesObject(doc.RootElement));
    }
}

