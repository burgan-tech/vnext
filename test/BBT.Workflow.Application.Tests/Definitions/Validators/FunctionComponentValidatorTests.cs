using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Runtime;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Unit tests for FunctionComponentValidator
/// </summary>
public class FunctionComponentValidatorTests
{
    private readonly FunctionComponentValidator _validator;

    public FunctionComponentValidatorTests()
    {
        _validator = new FunctionComponentValidator();
    }

    [Fact]
    public void CanHandle_ShouldReturnTrue_ForSysFunctions()
    {
        // Act
        var result = _validator.CanHandle(RuntimeSysSchemaInfo.Functions);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_ShouldReturnFalse_ForOtherTypes()
    {
        // Assert
        _validator.CanHandle(RuntimeSysSchemaInfo.Flows).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Tasks).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Views).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Schemas).ShouldBeFalse();
        _validator.CanHandle(RuntimeSysSchemaInfo.Extensions).ShouldBeFalse();
        _validator.CanHandle("unknown").ShouldBeFalse();
    }

    [Fact]
    public void Validate_ShouldReturnSuccess_ForValidFunction()
    {
        // Arrange
        var functionJson = """
        {
            "scope": "F",
            "task": {
                "type": "6",
                "config": {
                    "url": "https://example.com",
                    "method": "GET"
                }
            }
        }
        """;
        var attributes = JsonDocument.Parse(functionJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldReturnError_ForMissingTask()
    {
        // Arrange
        var functionJson = """
        {
            "scope": "F"
        }
        """;
        var attributes = JsonDocument.Parse(functionJson).RootElement;

        // Act
        var result = _validator.Validate(attributes);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("Function.Task"));
    }

    [Fact]
    public void Validate_ShouldReturnError_ForInvalidJson()
    {
        // Arrange
        var invalidJson = JsonDocument.Parse("\"not an object\"").RootElement;

        // Act
        var result = _validator.Validate(invalidJson);

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    // ── verbs / contract references ──────────────────────────────────────────

    private static JsonElement FunctionWith(string extraAttributes) =>
        JsonDocument.Parse($$"""
        {
            "scope": "F",
            "task": {
                "type": "6",
                "config": { "url": "https://example.com", "method": "GET" }
            }{{extraAttributes}}
        }
        """).RootElement;

    [Fact]
    public void Validate_ShouldReturnSuccess_WhenNoVerbsDeclared()
    {
        var result = _validator.Validate(FunctionWith(string.Empty));

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldReturnSuccess_ForAllKnownVerbs()
    {
        var result = _validator.Validate(
            FunctionWith(""", "verbs": ["GET", "POST", "PATCH", "DELETE"]"""));

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldReturnError_ForQueryVerb_NotSupportedYet()
    {
        // QUERY is intentionally unsupported until Swagger/gateway tooling handles it.
        var result = _validator.Validate(FunctionWith(""", "verbs": ["QUERY"]"""));

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("Function.Verbs"));
    }

    [Fact]
    public void Validate_ShouldReturnError_ForUnknownVerb()
    {
        var result = _validator.Validate(FunctionWith(""", "verbs": ["PUT"]"""));

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("Function.Verbs"));
    }

    [Fact]
    public void Validate_ShouldNormalizeVerbCasing()
    {
        var result = _validator.Validate(FunctionWith(""", "verbs": ["post", " patch "]"""));

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldReturnSuccess_ForWellFormedContractReferences()
    {
        var result = _validator.Validate(FunctionWith("""
        , "verbs": ["POST"]
        , "inputSchema":  { "key": "in",  "domain": "d", "flow": "sys-schemas", "version": "1.0.0" }
        , "outputSchema": { "key": "out", "domain": "d", "flow": "sys-schemas", "version": "1.0.0" }
        , "inputView":    { "key": "vin", "domain": "d", "flow": "sys-views",   "version": "1.0.0" }
        , "outputView":   { "key": "vout","domain": "d", "flow": "sys-views",   "version": "1.0.0" }
        """));

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldReturnError_WhenInputSchemaReferencesWrongFlow()
    {
        var result = _validator.Validate(FunctionWith("""
        , "verbs": ["POST"]
        , "inputSchema": { "key": "in", "domain": "d", "flow": "sys-views", "version": "1.0.0" }
        """));

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("Function.InputSchema"));
    }

    [Fact]
    public void Validate_ShouldReturnError_WhenInputViewReferencesWrongFlow()
    {
        var result = _validator.Validate(FunctionWith("""
        , "inputView": { "key": "vin", "domain": "d", "flow": "sys-schemas", "version": "1.0.0" }
        """));

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("Function.InputView"));
    }

    [Fact]
    public void Validate_ShouldReturnError_WhenInputSchemaDeclaredButNoVerbCarriesABody()
    {
        var result = _validator.Validate(FunctionWith("""
        , "verbs": ["GET"]
        , "inputSchema": { "key": "in", "domain": "d", "flow": "sys-schemas", "version": "1.0.0" }
        """));

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.MemberNames.Contains("Function.InputSchema"));
    }

    [Fact]
    public void Validate_ShouldAllowInputSchema_WhenABodyCarryingVerbAccompaniesGet()
    {
        var result = _validator.Validate(FunctionWith("""
        , "verbs": ["GET", "POST"]
        , "inputSchema": { "key": "in", "domain": "d", "flow": "sys-schemas", "version": "1.0.0" }
        """));

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldAllowInputSchema_WhenNoVerbsDeclared()
    {
        var result = _validator.Validate(FunctionWith("""
        , "inputSchema": { "key": "in", "domain": "d", "flow": "sys-schemas", "version": "1.0.0" }
        """));

        result.IsValid.ShouldBeTrue();
    }
}
