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
    [InlineData("2")]
    [InlineData("2.1")]
    [InlineData("2.1.0")]
    [InlineData("2.1.0-pkg.1.0.0")]
    public void IsValidPointer_ShouldAccept(string? pointer)
    {
        DefaultVersionResolver.IsValidPointer(pointer).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("latest")]
    [InlineData("LATEST")]
    [InlineData("2.x")]
    [InlineData("v2")]
    public void IsValidPointer_ShouldReject(string pointer)
    {
        DefaultVersionResolver.IsValidPointer(pointer).ShouldBeFalse();
    }
}
