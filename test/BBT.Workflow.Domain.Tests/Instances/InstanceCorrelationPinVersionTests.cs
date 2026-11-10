using System;
using BBT.Workflow.Instances;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Instances;

/// <summary>
/// The correlation stores the child's RESOLVED flow version once the child has started, so later
/// reads address the definition the child actually runs instead of re-resolving the authored
/// reference ("1.0", "latest") against a catalog that may have moved on.
/// </summary>
public class InstanceCorrelationPinVersionTests
{
    private static InstanceCorrelation NewCorrelation(string? authoredVersion) =>
        InstanceCorrelation.Create(Guid.NewGuid(), Guid.NewGuid(), "parent-state", Guid.NewGuid(),
            "S", "core", "child-flow", authoredVersion);

    [Fact]
    public void PinSubFlowVersion_ShouldReplaceAuthoredVersion()
    {
        var correlation = NewCorrelation("1.0");

        correlation.PinSubFlowVersion("1.0.3");

        correlation.SubFlowVersion.ShouldBe("1.0.3");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void PinSubFlowVersion_ShouldThrow_WhenBlank(string? blank)
    {
        var correlation = NewCorrelation("1.0");

        Should.Throw<ArgumentException>(() => correlation.PinSubFlowVersion(blank!));

        correlation.SubFlowVersion.ShouldBe("1.0");
    }

    [Fact]
    public void PinSubFlowVersion_ShouldBeNoOp_WhenAlreadyEqual()
    {
        var correlation = NewCorrelation("1.0.3");
        var modifiedAt = correlation.ModifiedAt;

        correlation.PinSubFlowVersion("1.0.3");

        correlation.SubFlowVersion.ShouldBe("1.0.3");
        correlation.ModifiedAt.ShouldBe(modifiedAt);
    }
}
