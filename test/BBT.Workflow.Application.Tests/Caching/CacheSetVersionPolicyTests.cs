using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;
using static BBT.Workflow.Caching.CacheSetTestHarness;

namespace BBT.Workflow.Caching;

/// <summary>
/// Unit tests for the default-version policy inside <see cref="CacheSet{T}"/>'s floating resolution:
/// <c>latest</c> / MAJOR / MAJOR.MINOR requests resolve through the pointer and its ceiling, policy
/// failures are never negative-cached, and full versions and <c>sys-</c> keys bypass the policy.
/// </summary>
public class CacheSetVersionPolicyTests
{
    private static (CacheSetTestHarness Harness, FakeVersionPolicySource Policy) Create(string? pointer)
    {
        var policy = new FakeVersionPolicySource { Pointer = pointer };
        return (new CacheSetTestHarness(versionPolicy: policy), policy);
    }

    [Fact]
    public async Task Resolve_ShouldReturnPointer_ForLatest()
    {
        // Arrange
        var (harness, _) = Create("1.0.0");
        harness.Publish("1.0.0", "1.1.0");

        // Act
        var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, null);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Version.ShouldBe("1.0.0");
    }

    [Fact]
    public async Task Resolve_ShouldApplyCeiling_ForMajor()
    {
        // Arrange
        var (harness, _) = Create("1.0");
        harness.Publish("1.0.0", "1.0.2", "1.1.0");

        // Act
        var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Version.ShouldBe("1.0.2");
    }

    [Fact]
    public async Task Resolve_ShouldReturnNotActivated_WhenOnlyAboveCeiling()
    {
        // Arrange
        var (harness, _) = Create("1.0.0");
        harness.Publish("1.0.0", "1.1.0");

        // Act
        var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.1");

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("Instance:100051");
    }

    [Fact]
    public async Task Resolve_ShouldFailClosed_WhenPointerUnmatched()
    {
        // Arrange
        var (harness, _) = Create("9.9");
        harness.Publish("1.0.0", "1.1.0");

        // Act
        var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, null);

        // Assert - an unresolvable pointer must not silently fall back to the newest version.
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("Instance:100052");
        harness.ResolutionSpellingsInCache().ShouldBeEmpty();
    }

    [Fact]
    public async Task Resolve_ShouldNotNegativeCache_WhenPolicySourceFails()
    {
        // Arrange
        var (harness, policy) = Create("1.0.0");
        harness.Publish("1.0.0", "1.1.0");
        policy.Fail = true;

        // Act - the registry is down: the resolution fails rather than falling back to latest.
        var failed = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, null);

        // Assert
        failed.IsSuccess.ShouldBeFalse();
        harness.ResolutionSpellingsInCache().ShouldBeEmpty();

        // Act - the registry recovers; no invalidation in between, so only an uncached failure lets
        // this request reach the policy again.
        policy.Fail = false;
        var recovered = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, null);

        // Assert
        recovered.IsSuccess.ShouldBeTrue();
        recovered.Value!.Version.ShouldBe("1.0.0");
        policy.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Resolve_ShouldNotNegativeCache_PolicyErrors()
    {
        // Arrange
        var (harness, policy) = Create("1.0.0");
        harness.Publish("1.0.0", "1.1.0");

        var notActivated = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.1");
        notActivated.Error.Code.ShouldBe("Instance:100051");
        harness.ResolutionSpellingsInCache().ShouldNotContain("1.1");

        // Act - the default moves up to 1.1.0.
        policy.Pointer = "1.1.0";
        await harness.Sut.InvalidateAsync(TestDomain, TestKey, "latest");
        var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.1");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Version.ShouldBe("1.1.0");
    }

    [Fact]
    public async Task Publish_InLineVersion_WithPartialPointer_ShouldWarmCeilingNotMax()
    {
        // Arrange - default 1.0 while 1.1.0 is published but not activated.
        var (harness, _) = Create("1.0");
        harness.Publish("1.0.0", "1.1.0");

        // Act - a patch inside the default line ships.
        harness.AddPublished("1.0.1");
        await harness.Sut.SetAsync(CreateView("1.0.1"));
        harness.Backend.ResetCounts();

        // Assert - the warmed latest/"1" answers are the in-line patch, served without a backend load.
        var generation = await harness.CurrentGenerationAsync();
        (await harness.Cache.GetAsync<CacheEnvelope<View>>(harness.ResolutionKey(generation, "latest")))!
            .Version.ShouldBe("1.0.1");
        (await harness.Cache.GetAsync<CacheEnvelope<View>>(harness.ResolutionKey(generation, "1")))!
            .Version.ShouldBe("1.0.1");
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, null)).Value!.Version.ShouldBe("1.0.1");
        harness.Backend.LoadAllCallCount.ShouldBe(0);

        // Act - a version above the ceiling ships.
        harness.AddPublished("1.1.1");
        await harness.Sut.SetAsync(CreateView("1.1.1"));

        // Assert - warming keeps the ceiling; the not-activated "1.1" line is not warmed at all.
        generation = await harness.CurrentGenerationAsync();
        (await harness.Cache.GetAsync<CacheEnvelope<View>>(harness.ResolutionKey(generation, "latest")))!
            .Version.ShouldBe("1.0.1");
        (await harness.Cache.GetAsync<CacheEnvelope<View>>(harness.ResolutionKey(generation, "1")))!
            .Version.ShouldBe("1.0.1");
        (await harness.Cache.GetAsync<CacheEnvelope<View>>(harness.ResolutionKey(generation, "1.1"))).ShouldBeNull();
    }

    [Fact]
    public async Task PointerChange_AfterInvalidate_ShouldResolveNewDefault()
    {
        // Arrange
        var (harness, policy) = Create("1.0.0");
        harness.Publish("1.0.0", "1.1.0");
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, null)).Value!.Version.ShouldBe("1.0.0");

        // Act
        policy.Pointer = "1.1.0";
        await harness.Sut.InvalidateAsync(TestDomain, TestKey, "latest");
        var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, null);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Version.ShouldBe("1.1.0");
    }

    [Fact]
    public async Task FullVersion_ShouldBypassPolicy()
    {
        // Arrange - the pointer holds the flow at 1.0.0; the full version above it stays reachable.
        var (harness, policy) = Create("1.0.0-pkg.1.0.0");
        harness.Publish("1.0.0-pkg.1.0.0", "1.1.0-pkg.1.0.0");

        // Act
        var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.1.0-pkg.1.0.0");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Version.ShouldBe("1.1.0-pkg.1.0.0");
        policy.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Resolve_ShouldSkipPolicy_ForSysKeys()
    {
        // Arrange - system components are exempt, so a pointer can never recurse into the registry.
        const string sysKey = "sys-flows";
        var (harness, policy) = Create("1.0.0");
        harness.Backend.Versions = [CreateView("1.0.0", sysKey), CreateView("1.1.0", sysKey)];

        // Act
        var result = await harness.Sut.GetByVersionAsync(TestDomain, sysKey, "latest");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Version.ShouldBe("1.1.0");
        policy.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("1.1")]
    [InlineData("1.0.2")]
    [InlineData("2")]
    public async Task NoPolicy_ShouldMatchCurrentBehavior(string? requested)
    {
        // Arrange - a policy source with no pointer must answer exactly what no policy source answers.
        var (withPolicy, _) = Create(null);
        var withoutPolicy = new CacheSetTestHarness();
        withPolicy.Publish("1.0.0", "1.0.2", "1.1.0");
        withoutPolicy.Publish("1.0.0", "1.0.2", "1.1.0");

        // Act
        var expected = await withoutPolicy.Sut.GetByVersionAsync(TestDomain, TestKey, requested);
        var actual = await withPolicy.Sut.GetByVersionAsync(TestDomain, TestKey, requested);

        // Assert
        actual.IsSuccess.ShouldBe(expected.IsSuccess);
        actual.Value?.Version.ShouldBe(expected.Value?.Version);
        if (!expected.IsSuccess)
            actual.Error.Code.ShouldBe(expected.Error.Code);
        withPolicy.ResolutionSpellingsInCache().ShouldBe(withoutPolicy.ResolutionSpellingsInCache());
    }
}
