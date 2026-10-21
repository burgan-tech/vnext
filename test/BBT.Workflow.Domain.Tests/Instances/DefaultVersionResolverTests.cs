using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for <see cref="DefaultVersionResolver"/>.
/// </summary>
public class DefaultVersionResolverTests
{
    private static readonly string[] Published = ["2.0.0", "2.1.0", "2.1.3", "2.2.0", "2.3.0", "3.0.0"];

    [Theory]
    [InlineData("latest", null)]
    [InlineData("LATEST", null)]
    [InlineData("latest", "2")]
    [InlineData("LATEST", "2")]
    [InlineData("latest", "2.2.0")]
    [InlineData("LATEST", "2.2.0")]
    public void Resolve_ShouldTreatLatestPointerAsAutomatic(string pointer, string? requested)
    {
        var expected = DefaultVersionResolver.Resolve(Published, null, requested);

        var result = DefaultVersionResolver.Resolve(Published, pointer, requested);

        result.ShouldBe(expected);
        result.Source.ShouldBe(VersionResolutionSource.Automatic);
        result.EffectiveDefault.ShouldBeNull();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("latest", true)]
    [InlineData(" LATEST ", true)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("2.1", false)]
    public void IsAutomaticPointer_ShouldMatchNullAndLatestOnly(string? pointer, bool expected)
    {
        DefaultVersionResolver.IsAutomaticPointer(pointer).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null, null, "3.0.0", VersionResolutionOutcome.Resolved, VersionResolutionSource.Automatic)]
    [InlineData(null, "2", "2.3.0", VersionResolutionOutcome.Resolved, VersionResolutionSource.Automatic)]
    [InlineData("2.1.0", null, "2.1.0", VersionResolutionOutcome.Resolved, VersionResolutionSource.Default)]
    [InlineData("2.1.0", "latest", "2.1.0", VersionResolutionOutcome.Resolved, VersionResolutionSource.Default)]
    [InlineData("2.1.0", "2.1", "2.1.0", VersionResolutionOutcome.Resolved, VersionResolutionSource.Ceiling)]
    [InlineData("2.1.0", "2.2.0", "2.2.0", VersionResolutionOutcome.Resolved, VersionResolutionSource.Explicit)]
    [InlineData("2.1.0", "2.2", null, VersionResolutionOutcome.NotActivated, VersionResolutionSource.Ceiling)]
    [InlineData("2.1", null, "2.1.3", VersionResolutionOutcome.Resolved, VersionResolutionSource.Default)]
    [InlineData("2.1", "2", "2.1.3", VersionResolutionOutcome.Resolved, VersionResolutionSource.Ceiling)]
    [InlineData("2.1", "2.1.0", "2.1.0", VersionResolutionOutcome.Resolved, VersionResolutionSource.Explicit)]
    [InlineData("2", null, "2.3.0", VersionResolutionOutcome.Resolved, VersionResolutionSource.Default)]
    [InlineData("2", "3", null, VersionResolutionOutcome.NotActivated, VersionResolutionSource.Ceiling)]
    [InlineData("2", "4", null, VersionResolutionOutcome.NotFound, VersionResolutionSource.Ceiling)]
    [InlineData("2.4", null, null, VersionResolutionOutcome.PointerInvalid, VersionResolutionSource.Default)]
    [InlineData("2.4", "2", null, VersionResolutionOutcome.PointerInvalid, VersionResolutionSource.Default)]
    [InlineData("2.4", "2.2.0", "2.2.0", VersionResolutionOutcome.Resolved, VersionResolutionSource.Explicit)]
    [InlineData("2.4", "2.2.0-pkg.1.0.0", null, VersionResolutionOutcome.NotFound, VersionResolutionSource.Explicit)]
    [InlineData("2.1.0", "9.9.9", null, VersionResolutionOutcome.NotFound, VersionResolutionSource.Explicit)]
    public void Resolve_ShouldFollowPolicyTable(
        string? pointer,
        string? requested,
        string? expectedVersion,
        VersionResolutionOutcome expectedOutcome,
        VersionResolutionSource expectedSource)
    {
        var result = DefaultVersionResolver.Resolve(Published, pointer, requested);

        result.Version.ShouldBe(expectedVersion);
        result.Outcome.ShouldBe(expectedOutcome);
        result.Source.ShouldBe(expectedSource);
    }

    [Theory]
    [InlineData("2.1.0-pkg.1.0.168", null, "2.1.0-pkg.1.0.168+core", VersionResolutionSource.Default)]
    [InlineData("2.1.0-pkg.1.0.168", "2.1", "2.1.0-pkg.1.0.168+core", VersionResolutionSource.Ceiling)]
    [InlineData("2.1.0-pkg.1.0.168", "2.1.0-pkg.1.0.169", "2.1.0-pkg.1.0.169+core", VersionResolutionSource.Explicit)]
    [InlineData("2.1.0-pkg.1.0.168+core", null, "2.1.0-pkg.1.0.168+core", VersionResolutionSource.Default)]
    public void Resolve_ShouldMatchFullPointerIgnoringBuildMetadata(
        string pointer,
        string? requested,
        string expectedVersion,
        VersionResolutionSource expectedSource)
    {
        string[] published = ["2.1.0-pkg.1.0.168+core", "2.1.0-pkg.1.0.169+core", "2.2.0-pkg.1.0.170+core"];

        var result = DefaultVersionResolver.Resolve(published, pointer, requested);

        result.Version.ShouldBe(expectedVersion);
        result.Outcome.ShouldBe(VersionResolutionOutcome.Resolved);
        result.Source.ShouldBe(expectedSource);
    }

    [Fact]
    public void Resolve_ShouldReportPointerInvalid_WhenFullPointerMatchesNoBuild()
    {
        string[] published = ["2.1.0-pkg.1.0.168+core", "2.1.0-pkg.1.0.169+core"];

        var result = DefaultVersionResolver.Resolve(published, "2.1.0-pkg.1.0.999", requested: null);

        result.Outcome.ShouldBe(VersionResolutionOutcome.PointerInvalid);
    }

    [Theory]
    [InlineData("2.1.0", null, "2.1.0-pkg.1.1.0", VersionResolutionSource.Default)]
    [InlineData("2.1.0-pkg.1.0.0", "2", "2.1.0-pkg.1.0.0", VersionResolutionSource.Ceiling)]
    [InlineData("2.1", "2.2.0-pkg.1.0.0", "2.2.0-pkg.1.0.0", VersionResolutionSource.Explicit)]
    public void Resolve_ShouldHandlePackageForms(
        string pointer,
        string? requested,
        string expectedVersion,
        VersionResolutionSource expectedSource)
    {
        string[] published = ["2.1.0-pkg.1.0.0", "2.1.0-pkg.1.1.0", "2.2.0-pkg.1.0.0"];

        var result = DefaultVersionResolver.Resolve(published, pointer, requested);

        result.Version.ShouldBe(expectedVersion);
        result.Outcome.ShouldBe(VersionResolutionOutcome.Resolved);
        result.Source.ShouldBe(expectedSource);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("LATEST")]
    [InlineData("2")]
    [InlineData("2.1")]
    [InlineData("2.1.0")]
    [InlineData("2.1.0-pkg.1.0.0")]
    [InlineData("2.1.0-pkg.1.0.168")]
    [InlineData("2.1.0-pkg.1.0.168+core")]
    public void IsValidPointer_ShouldAccept(string? pointer)
    {
        DefaultVersionResolver.IsValidPointer(pointer).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("2.x")]
    [InlineData("v2")]
    [InlineData("2.1.0-pkg.1.0")]
    [InlineData("2.1.0-pkg.1")]
    public void IsValidPointer_ShouldReject(string pointer)
    {
        DefaultVersionResolver.IsValidPointer(pointer).ShouldBeFalse();
    }
}
