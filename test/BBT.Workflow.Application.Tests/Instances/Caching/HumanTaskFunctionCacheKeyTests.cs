using System.Collections.Generic;
using BBT.Aether.Users;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// The human-task cache entry holds <c>humanTask</c> text, which can be customer-identifying, and
/// the list it holds was already filtered for one caller. That is only safe while the key covers
/// EVERY input the authorization decision reads — anything it misses is a way for one caller scope
/// to be served another's answer.
/// </summary>
public class HumanTaskFunctionCacheKeyTests
{
    private const string Domain = "test-domain";

    private static HumanTaskFunctionCache Create(string? userId = "u1", string? actor = "u1")
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns(userId);
        currentUser.ActorUserName.Returns(actor);

        return new HumanTaskFunctionCache(
            Substitute.For<BBT.Aether.DistributedCache.IDistributedCacheService>(),
            currentUser,
            Options.Create(new HumanTaskFunctionCacheOptions()),
            Substitute.For<ILogger<HumanTaskFunctionCache>>());
    }

    [Fact]
    public void TheSameCallerAndHeadersProduceTheSameKey()
    {
        var cache = Create();
        var headers = new Dictionary<string, string?> { ["x-branch"] = "34" };

        cache.BuildKey(Domain, ["clerk"], headers)
            .ShouldBe(cache.BuildKey(Domain, ["clerk"], headers));
    }

    [Fact]
    public void DifferentRolesProduceDifferentKeys()
    {
        var cache = Create();

        cache.BuildKey(Domain, ["clerk"], null)
            .ShouldNotBe(cache.BuildKey(Domain, ["senior.approver"], null));
    }

    [Fact]
    public void DifferentDomainsProduceDifferentKeys()
    {
        var cache = Create();

        cache.BuildKey(Domain, ["clerk"], null)
            .ShouldNotBe(cache.BuildKey("other-domain", ["clerk"], null));
    }

    [Fact]
    public void TwoCallersWithIdenticalRolesStillGetDifferentKeys()
    {
        // $InstanceStarter and $PreviousUser match against the actor, not the role set, so two
        // callers holding the same roles can legitimately see different lists.
        Create(userId: "u1", actor: "u1").BuildKey(Domain, ["clerk"], null)
            .ShouldNotBe(Create(userId: "u2", actor: "u2").BuildKey(Domain, ["clerk"], null));
    }

    /// <summary>
    /// The reason the key is not just the shared caller scope. A dynamic role grant resolves
    /// <c>$.context.Headers.*</c> against a header name the WORKFLOW AUTHOR picked, so the set of
    /// headers that can change the answer cannot be enumerated when the key is built. Folding them
    /// all in costs hit rate; leaving them out costs correctness, in the direction of disclosure.
    /// </summary>
    [Fact]
    public void AHeaderADynamicGrantCouldReadChangesTheKey()
    {
        var cache = Create();

        cache.BuildKey(Domain, ["clerk"], new Dictionary<string, string?> { ["x-branch"] = "34" })
            .ShouldNotBe(
                cache.BuildKey(Domain, ["clerk"], new Dictionary<string, string?> { ["x-branch"] = "77" }));
    }

    /// <summary>
    /// Per-request noise must not be in the key or the hit rate would be zero. This list is the one
    /// tuning knob, and widening it is a security decision rather than a performance one.
    /// </summary>
    [Theory]
    [InlineData("traceparent")]
    [InlineData("x-request-id")]
    [InlineData("user-agent")]
    public void VolatileHeadersAreExcluded(string headerName)
    {
        var cache = Create();

        cache.BuildKey(Domain, ["clerk"], new Dictionary<string, string?> { [headerName] = "a" })
            .ShouldBe(
                cache.BuildKey(Domain, ["clerk"], new Dictionary<string, string?> { [headerName] = "b" }));
    }

    [Fact]
    public void HeaderOrderAndCasingDoNotChangeTheKey()
    {
        var cache = Create();

        var first = new Dictionary<string, string?> { ["x-branch"] = "34", ["x-segment"] = "retail" };
        var second = new Dictionary<string, string?> { ["X-Segment"] = "retail", ["X-Branch"] = "34" };

        cache.BuildKey(Domain, ["clerk"], first).ShouldBe(cache.BuildKey(Domain, ["clerk"], second));
    }
}
