using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Aether.Users;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Authorization;

/// <summary>
/// The default provider must be behaviour-identical to what every surface did inline before the
/// resolver existed: <c>ICurrentUser.Roles</c> first, the legacy <c>role</c> header as fallback.
/// A drift here silently changes authorization for every deployment that has not opted into another
/// provider — which is all of them by default.
/// </summary>
public sealed class DefaultCallerRoleResolverTests
{
    [Fact]
    public async Task PrefersTheCurrentUsersRoles_OverTheHeader()
    {
        var resolver = ResolverFor(["admin", "auditor"]);

        var result = await resolver.ResolveRolesAsync(
            new Dictionary<string, string?> { ["role"] = "ignored" });

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(["admin", "auditor"]);
    }

    [Fact]
    public async Task FallsBackToTheRoleHeader_WhenTheUserCarriesNone()
    {
        var resolver = ResolverFor(null);

        var result = await resolver.ResolveRolesAsync(
            new Dictionary<string, string?> { ["role"] = "approver, auditor" });

        result.Value.ShouldBe(["approver", "auditor"]);
    }

    [Fact]
    public async Task ReturnsNull_WhenNeitherSourceCarriesRoles()
    {
        var result = await ResolverFor(null).ResolveRolesAsync(null);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    /// <summary>
    /// The default provider does no I/O, so it has no way to fail. Pinned because every call site now
    /// carries a failure branch that only a remote provider can reach — if this ever started failing,
    /// those branches would fire on the default path too.
    /// </summary>
    [Fact]
    public async Task NeverFails()
    {
        (await ResolverFor(null).ResolveRolesAsync(null)).IsSuccess.ShouldBeTrue();
        (await ResolverFor([]).ResolveRolesAsync(null)).IsSuccess.ShouldBeTrue();
        (await ResolverFor(["x"]).ResolveRolesAsync(null)).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(new string[0], null)]
    [InlineData(new[] { "solo" }, "solo")]
    [InlineData(new[] { "first", "second" }, "first")]
    public void SingleRoleOf_IsAlwaysTheFirstOfTheSet(string[]? roles, string? expected) =>
        ICallerRoleResolver.SingleRoleOf(roles).ShouldBe(expected);

    private static DefaultCallerRoleResolver ResolverFor(string[]? roles)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Roles.Returns(roles);
        return new DefaultCallerRoleResolver(user);
    }
}
