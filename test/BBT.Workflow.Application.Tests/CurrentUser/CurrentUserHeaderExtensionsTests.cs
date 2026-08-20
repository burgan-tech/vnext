using System.Collections.Generic;
using BBT.Aether.Users;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.CurrentUser;

/// <summary>
/// Tests for the caller role/roles resolution helpers used when locally routing function services:
/// prefer the current user's roles, fall back to the request <c>role</c> header, else null.
/// </summary>
public class CurrentUserHeaderExtensionsTests
{
    private static ICurrentUser UserWithRoles(params string[]? roles)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Roles.Returns(roles);
        return user;
    }

    [Fact]
    public void ResolveCallerRoles_ReturnsAllUserRoles()
    {
        var user = UserWithRoles("admin", "ops");
        user.ResolveCallerRoles(new Dictionary<string, string?> { ["role"] = "ignored" })
            .ShouldBe(new[] { "admin", "ops" });
    }

    [Fact]
    public void ResolveCallerRoles_FallsBackToParsedHeader_WhenUserHasNoRoles()
    {
        var user = UserWithRoles();
        var headers = new Dictionary<string, string?> { ["role"] = "approver auditor" };
        user.ResolveCallerRoles(headers).ShouldBe(new[] { "approver", "auditor" });
    }

    [Fact]
    public void ResolveCallerRoles_Null_WhenNeitherPresent()
    {
        UserWithRoles().ResolveCallerRoles(null).ShouldBeNull();
    }
}
