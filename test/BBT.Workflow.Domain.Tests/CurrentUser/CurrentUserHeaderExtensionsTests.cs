using System;
using System.Collections.Generic;
using BBT.Aether.Users;
using BBT.Workflow.CurrentUser;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.CurrentUser;

public sealed class CurrentUserHeaderExtensionsTests
{
    [Fact]
    public void ChangeFromHeaders_WhenHeadersIsNull_ReturnsNoOpDisposable_AndDoesNotCallChange()
    {
        var currentUser = Substitute.For<ICurrentUser>();

        using (currentUser.ChangeFromHeaders(null))
        {
            // Scope active; dispose should not throw
        }

        currentUser.DidNotReceive().Change(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string[]?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>());
    }

    [Fact]
    public void ChangeFromHeaders_WhenHeadersIsEmpty_ReturnsNoOpDisposable_AndDoesNotCallChange()
    {
        var currentUser = Substitute.For<ICurrentUser>();
        var headers = new Dictionary<string, string?>();

        using (currentUser.ChangeFromHeaders(headers))
        {
        }

        currentUser.DidNotReceive().Change(
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string[]?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>());
    }

    [Fact]
    public void ChangeFromHeaders_WhenHeadersContainClaimValues_CallsChangeWithMappedValues()
    {
        var currentUser = Substitute.For<ICurrentUser>();
        var disposable = Substitute.For<IDisposable>();
        currentUser
            .Change(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string[]?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>())
            .Returns(disposable);

        var headers = new Dictionary<string, string?>
        {
            ["userId"] = "uid-1",
            ["sub"] = "user1",
            ["given_name"] = "Given",
            ["family_name"] = "Family",
            ["role"] = "Admin,User",
            ["act_uid"] = "actor-uid",
            ["act_sub"] = "actor1",
            ["consent_id"] = "consent-1"
        };

        using (currentUser.ChangeFromHeaders(headers))
        {
        }

        currentUser.Received(1).Change(
            "uid-1",
            "user1",
            "Given",
            "Family",
            Arg.Is<string[]>(r => r.Length == 2 && r[0] == "Admin" && r[1] == "User"),
            "actor-uid",
            "actor1",
            "consent-1");
        disposable.Received(1).Dispose();
    }

    [Fact]
    public void ChangeFromHeaders_WhenOnlySubAndActSubPresent_CallsChangeWithNullsForMissingKeys()
    {
        var currentUser = Substitute.For<ICurrentUser>();
        var disposable = Substitute.For<IDisposable>();
        currentUser
            .Change(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string[]?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>())
            .Returns(disposable);

        var headers = new Dictionary<string, string?>
        {
            ["sub"] = "user1",
            ["act_sub"] = "actor1"
        };

        using (currentUser.ChangeFromHeaders(headers))
        {
        }

        currentUser.Received(1).Change(
            null,
            "user1",
            null,
            null,
            null,
            null,
            "actor1",
            null);
    }

    [Fact]
    public void ChangeFromHeaders_WhenRoleHeaderEmpty_SendsNullRoles()
    {
        var currentUser = Substitute.For<ICurrentUser>();
        var disposable = Substitute.For<IDisposable>();
        currentUser
            .Change(
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string[]?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>())
            .Returns(disposable);

        var headers = new Dictionary<string, string?>
        {
            ["sub"] = "user1",
            ["role"] = ""
        };

        using (currentUser.ChangeFromHeaders(headers))
        {
        }

        currentUser.Received(1).Change(
            null,
            "user1",
            null,
            null,
            null,
            null,
            null,
            null);
    }

    [Fact]
    public void ParseRolesFromHeader_WhenCommaOrSpaceSeparated_ReturnsTrimmedRoles()
    {
        CurrentUserHeaderExtensions.ParseRolesFromHeader("Admin,User").ShouldBe(new[] { "Admin", "User" });
        CurrentUserHeaderExtensions.ParseRolesFromHeader("Admin User").ShouldBe(new[] { "Admin", "User" });
        CurrentUserHeaderExtensions.ParseRolesFromHeader("Admin , User , Maker").ShouldBe(new[] { "Admin", "User", "Maker" });
        CurrentUserHeaderExtensions.ParseRolesFromHeader("  a  b  c  ").ShouldBe(new[] { "a", "b", "c" });
    }

    [Fact]
    public void ParseRolesFromHeader_WhenNullOrWhiteSpace_ReturnsNull()
    {
        CurrentUserHeaderExtensions.ParseRolesFromHeader(null).ShouldBeNull();
        CurrentUserHeaderExtensions.ParseRolesFromHeader("").ShouldBeNull();
        CurrentUserHeaderExtensions.ParseRolesFromHeader("   ").ShouldBeNull();
    }
}
