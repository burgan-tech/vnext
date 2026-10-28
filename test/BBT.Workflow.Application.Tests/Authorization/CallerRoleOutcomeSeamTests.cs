using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Users;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Row security must tell "role resolution failed" from "no roles". A provider that does not
/// implement the new member reports Failed (fail-closed); the default provider never fails.
/// </summary>
public sealed class CallerRoleOutcomeSeamTests
{
    [Fact]
    public async Task DefaultInterfaceMember_ReportsFailed()
    {
        ICallerRoleResolver fake = new OnlyResolveRolesFake([]);

        var resolution = await fake.ResolveRoleSetAsync(null);

        resolution.Outcome.ShouldBe(CallerRoleOutcome.Failed);
        resolution.Roles.ShouldBeEmpty();
        resolution.FailureKind.ShouldBe("unverified");
    }

    [Fact]
    public async Task DefaultProvider_WithRoleHeader_IsResolved()
    {
        var resolution = await ResolverFor(null).ResolveRoleSetAsync(
            new Dictionary<string, string?> { ["role"] = "branch-staff" });

        resolution.Roles.ShouldBe(["branch-staff"]);
        resolution.Outcome.ShouldBe(CallerRoleOutcome.Resolved);
    }

    [Fact]
    public async Task DefaultProvider_WithoutRoles_IsEmpty()
    {
        var resolution = await ResolverFor(null).ResolveRoleSetAsync(null);

        resolution.Roles.ShouldBeEmpty();
        resolution.Outcome.ShouldBe(CallerRoleOutcome.Empty);
    }

    [Fact]
    public async Task DefaultProvider_ResolveRolesAsync_Unchanged()
    {
        var resolver = ResolverFor(null);
        var headers = new Dictionary<string, string?> { ["role"] = "approver, auditor" };

        (await resolver.ResolveRolesAsync(headers)).Value.ShouldBe(["approver", "auditor"]);
        (await resolver.ResolveRolesAsync(null)).Value.ShouldBeNull();
    }

    private static DefaultCallerRoleResolver ResolverFor(string[]? userRoles)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Roles.Returns(userRoles);
        return new DefaultCallerRoleResolver(user);
    }

    private sealed class OnlyResolveRolesFake(string[] roles) : ICallerRoleResolver
    {
        public Task<Result<string[]?>> ResolveRolesAsync(
            IReadOnlyDictionary<string, string?>? headers,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string[]?>.Ok(roles));
    }
}
