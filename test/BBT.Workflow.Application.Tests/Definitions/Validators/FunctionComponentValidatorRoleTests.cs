using System;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Function roles were never validated at publish; they now share the dynamic-role rules with the workflow.
/// </summary>
public class FunctionComponentValidatorRoleTests
{
    private readonly FunctionComponentValidator _validator = new();

    private static JsonElement FunctionWithRoles(string rolesJson) => JsonDocument.Parse($$"""
    {
        "scope": "F",
        "roles": {{rolesJson}},
        "task": { "type": "6", "config": { "url": "https://example.com", "method": "GET" } }
    }
    """).RootElement;

    [Theory]
    [InlineData("""[{"role":"$user.$CreatedBy","grant":"allow"}]""", "invalid path")]
    [InlineData("""[{"grant":"allow","allOf":[{"role":"$user.$CreatedBy"}]}]""", "invalid path")]
    [InlineData("""[{"grant":"allow","anyOf":[{"role":"$user.$.context."}]}]""", "empty navigation path")]
    public void Validate_ShouldFail_WhenFunctionRoleIsMalformedDynamicRole(string roles, string expected)
    {
        var result = _validator.Validate(FunctionWithRoles(roles));

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e => e.ErrorMessage!.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldPass_WhenFunctionRolesAreWellFormed()
    {
        var result = _validator.Validate(FunctionWithRoles(
            """[{"role":"ops","grant":"allow"},{"grant":"allow","allOf":[{"role":"a"},{"role":"$user.$.context.owner"}]}]"""));

        result.IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ShouldReturnValidationError_WhenRoleGrantShapeIsMalformed()
    {
        var result = _validator.Validate(FunctionWithRoles(
            """[{"role":"a","grant":"allow","anyOf":[{"role":"b"}]}]"""));

        result.IsValid.ShouldBeFalse();
        result.ValidationErrors.ShouldContain(e =>
            e.ErrorMessage!.Contains("exactly one of 'role', 'allOf' or 'anyOf'", StringComparison.Ordinal));
    }
}
