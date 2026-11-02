using BBT.Workflow.Definitions.RowSecurity;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.RowSecurity;

/// <summary>
/// Unit tests for <see cref="RowParameterPath"/> parsing of <c>$.context.*</c> row-security parameters.
/// </summary>
public sealed class RowParameterPathTests
{
    [Theory]
    [InlineData("$.context.Headers.x-branch-code", RowParameterSource.Headers, "x-branch-code")]
    [InlineData("$.context.QueryParameters.branch", RowParameterSource.QueryParameters, "branch")]
    [InlineData("$.context.CurrentUser.ActorUserName", RowParameterSource.CurrentUser, "ActorUserName")]
    [InlineData("$.context.CurrentUser.ActorUserId", RowParameterSource.CurrentUser, "ActorUserId")]
    [InlineData("$.context.CurrentUser.UserName", RowParameterSource.CurrentUser, "UserName")]
    [InlineData("$.context.CurrentUser.Position", RowParameterSource.CurrentUser, "Position")]
    public void TryParse_KnownSources(string text, RowParameterSource source, string name)
    {
        RowParameterPath.TryParse(text, out var path, out var error).ShouldBeTrue();
        path.ShouldBe(new RowParameterPath(source, name));
        error.ShouldBeNull();
    }

    [Theory]
    [InlineData("$.context.Route.x", "RS-E07")]
    [InlineData("$.context.Query.x", "RS-E07")]
    [InlineData("$.context.CurrentUser.Email", "RS-E07")]
    [InlineData("$.context.Headers.", "RS-E07")]
    [InlineData("$.context.Headers", "RS-E07")]
    [InlineData("$.context.", "RS-E07")]
    [InlineData("$.context.headers.x", "RS-E07")]
    [InlineData("$.Context.Headers.x", null)]
    [InlineData("plain-literal", null)]
    public void TryParse_Rejects(string text, string? code)
    {
        RowParameterPath.TryParse(text, out var path, out var error).ShouldBeFalse();
        path.ShouldBeNull();
        error.ShouldBe(code);
        RowParameterPath.LooksLikeParameter(text).ShouldBe(code != null);
    }

    [Fact]
    public void LooksLikeParameter_Null_IsFalse()
    {
        RowParameterPath.LooksLikeParameter(null).ShouldBeFalse();
    }

    [Fact]
    public void CurrentUserFields_AreTheFourIdentityFields()
    {
        RowParameterPath.CurrentUserFields.ShouldBe(
            new[] { "ActorUserName", "ActorUserId", "UserName", "Position" }, ignoreOrder: true);
    }
}
