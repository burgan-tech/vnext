using BBT.Aether.Users;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// <see cref="CallerScopeHash"/> must tell apart callers whose cached response can differ. The
/// behalf-of subject (<c>sub</c>) is read by <c>$InstanceBehalfOfStarter</c> / <c>$userBehalfOf.</c>
/// grants, so two callers acting for different subjects must never share a cache entry or ETag.
/// </summary>
public sealed class CallerScopeHashTests
{
    private static ICurrentUser User(string actor, string sub)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns("id-1");
        user.ActorUserName.Returns(actor);
        user.UserName.Returns(sub);
        return user;
    }

    [Fact]
    public void Different_sub_same_act_sub_yields_different_scope()
    {
        var a = User(actor: "u-ops", sub: "c-acme");
        var b = User(actor: "u-ops", sub: "c-other");

        CallerScopeHash.Compute(a, null, null, null, null, null)
            .ShouldNotBe(CallerScopeHash.Compute(b, null, null, null, null, null));
    }

    [Fact]
    public void Same_actor_and_sub_yields_same_scope()
    {
        CallerScopeHash.Compute(User("u-ops", "c-acme"), null, null, null, null, null)
            .ShouldBe(CallerScopeHash.Compute(User("u-ops", "c-acme"), null, null, null, null, null));
    }
}
