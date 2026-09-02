using System;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for InstanceCorrelation
/// </summary>
public class InstanceCorrelationTests : DomainTestBase<DomainEntryPoint>
{
    [Fact]
    public void Create_ShouldInitializeAllProperties()
    {
        // Arrange
        var id = Guid.NewGuid();
        var parentInstanceId = Guid.NewGuid();
        var parentState = "parent-state";
        var subFlowInstanceId = Guid.NewGuid();
        var subFlowType = "S";
        var subFlowDomain = "test-domain";
        var subFlowName = "test-flow";
        var subFlowVersion = "1.0.0";

        // Act
        var correlation = InstanceCorrelation.Create(
            id,
            parentInstanceId,
            parentState,
            subFlowInstanceId,
            subFlowType,
            subFlowDomain,
            subFlowName,
            subFlowVersion
        );

        // Assert
        Assert.Equal(id, correlation.Id);
        Assert.Equal(parentInstanceId, correlation.ParentInstanceId);
        Assert.Equal(parentState, correlation.ParentState);
        Assert.Equal(subFlowInstanceId, correlation.SubFlowInstanceId);
        Assert.Equal(SubFlowType.SubFlow, correlation.SubFlowType);
        Assert.Equal(subFlowDomain, correlation.SubFlowDomain);
        Assert.Equal(subFlowName, correlation.SubFlowName);
        Assert.Equal(subFlowVersion, correlation.SubFlowVersion);
        Assert.False(correlation.IsCompleted);
        Assert.Null(correlation.CompletedAt);
    }

    [Fact]
    public void Create_ShouldHandleNullVersion()
    {
        // Arrange
        var id = Guid.NewGuid();
        var parentInstanceId = Guid.NewGuid();
        var parentState = "parent-state";
        var subFlowInstanceId = Guid.NewGuid();
        var subFlowType = "S";
        var subFlowDomain = "test-domain";
        var subFlowName = "test-flow";

        // Act
        var correlation = InstanceCorrelation.Create(
            id,
            parentInstanceId,
            parentState,
            subFlowInstanceId,
            subFlowType,
            subFlowDomain,
            subFlowName,
            null
        );

        // Assert
        Assert.Null(correlation.SubFlowVersion);
    }

    [Theory]
    [InlineData("S")]
    [InlineData("P")]
    public void Create_ShouldParseSubFlowType(string typeCode)
    {
        // Arrange & Act
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "state",
            Guid.NewGuid(),
            typeCode,
            "domain",
            "flow",
            null
        );

        // Assert
        Assert.NotNull(correlation.SubFlowType);
        Assert.Equal(typeCode, correlation.SubFlowType.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Create_ShouldThrow_WhenParentStateIsInvalid(string? parentState)
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentException>(() =>
            InstanceCorrelation.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                parentState!,
                Guid.NewGuid(),
                "S",
                "domain",
                "flow",
                null
            ));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Create_ShouldThrow_WhenSubFlowDomainIsInvalid(string? subFlowDomain)
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentException>(() =>
            InstanceCorrelation.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "state",
                Guid.NewGuid(),
                "S",
                subFlowDomain!,
                "flow",
                null
            ));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Create_ShouldThrow_WhenSubFlowNameIsInvalid(string? subFlowName)
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentException>(() =>
            InstanceCorrelation.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "state",
                Guid.NewGuid(),
                "S",
                "domain",
                subFlowName!,
                null
            ));
    }

    [Fact]
    public void Completed_ShouldSetIsCompletedToTrue()
    {
        // Arrange
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "state",
            Guid.NewGuid(),
            "S",
            "domain",
            "flow",
            null
        );

        Assert.False(correlation.IsCompleted);
        Assert.Null(correlation.CompletedAt);

        // Act
        correlation.Completed();

        // Assert
        Assert.True(correlation.IsCompleted);
        Assert.NotNull(correlation.CompletedAt);
    }

    [Fact]
    public void Completed_ShouldSetCompletedAtToCurrentTime()
    {
        // Arrange
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "state",
            Guid.NewGuid(),
            "S",
            "domain",
            "flow",
            null
        );
        var before = DateTime.UtcNow.AddSeconds(-1);

        // Act
        correlation.Completed();
        var after = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.NotNull(correlation.CompletedAt);
        Assert.True(correlation.CompletedAt >= before && correlation.CompletedAt <= after);
    }

    [Fact]
    public void MarkSettled_ShouldRequireTerminalCorrelation_AndRevertShouldClearMarker()
    {
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(), Guid.NewGuid(), "state", Guid.NewGuid(), "S", "domain", "flow", null);

        Assert.Throws<InvalidOperationException>(() => correlation.MarkSettled(DateTime.UtcNow));

        var settledAt = DateTime.UtcNow;
        correlation.ApplyTerminalOutcome(SubItemTerminalOutcome.Completed, settledAt);
        correlation.MarkSettled(settledAt);
        correlation.SettledAt.ShouldBe(settledAt);

        correlation.Revert();
        correlation.SettledAt.ShouldBeNull();
    }

    [Fact]
    public void Correlation_PropertiesShouldBeAccessible()
    {
        // Arrange
        var id = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var subFlowId = Guid.NewGuid();
        
        var correlation = InstanceCorrelation.Create(
            id,
            parentId,
            "state",
            subFlowId,
            "S",
            "domain",
            "flow",
            "1.0.0"
        );
        correlation.Completed();

        // Assert - Verify all properties are accessible
        Assert.Equal(id, correlation.Id);
        Assert.Equal(parentId, correlation.ParentInstanceId);
        Assert.Equal("state", correlation.ParentState);
        Assert.Equal(subFlowId, correlation.SubFlowInstanceId);
        Assert.Equal("domain", correlation.SubFlowDomain);
        Assert.Equal("flow", correlation.SubFlowName);
        Assert.Equal("1.0.0", correlation.SubFlowVersion);
        Assert.Equal(SubFlowType.SubFlow, correlation.SubFlowType);
        Assert.True(correlation.IsCompleted);
        Assert.NotNull(correlation.CompletedAt);
    }

    [Fact]
    public void Correlation_BeforeCompletion_ShouldHaveCorrectState()
    {
        // Arrange & Act
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "state",
            Guid.NewGuid(),
            "P",
            "domain",
            "flow",
            null
        );

        // Assert
        Assert.False(correlation.IsCompleted);
        Assert.Null(correlation.CompletedAt);
        Assert.Equal(SubFlowType.SubProcess, correlation.SubFlowType);
    }

    [Fact]
    public void Completed_ShouldBeIdempotent()
    {
        // Arrange
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "state",
            Guid.NewGuid(),
            "S",
            "domain",
            "flow",
            null
        );

        // Act
        correlation.Completed();
        var firstCompletedAt = correlation.CompletedAt;

        correlation.Completed();
        var secondCompletedAt = correlation.CompletedAt;

        // Assert
        Assert.True(correlation.IsCompleted);
        Assert.Equal(firstCompletedAt, secondCompletedAt);
        Assert.Equal(SubItemTerminalOutcome.Completed, correlation.TerminalOutcome);
    }

    [Fact]
    public void ApplyTerminalOutcome_ShouldPersistFirstOutcome_AndRejectConflict()
    {
        var correlation = CreateCorrelation("S");
        var completedAt = DateTime.UtcNow;

        correlation.ApplyTerminalOutcome(SubItemTerminalOutcome.Faulted, completedAt)
            .ShouldBe(TerminalOutcomeApplyResult.Applied);
        correlation.ApplyTerminalOutcome(SubItemTerminalOutcome.Faulted, completedAt.AddSeconds(1))
            .ShouldBe(TerminalOutcomeApplyResult.Duplicate);
        correlation.ApplyTerminalOutcome(SubItemTerminalOutcome.Canceled, completedAt.AddSeconds(2))
            .ShouldBe(TerminalOutcomeApplyResult.Conflict);

        correlation.TerminalOutcome.ShouldBe(SubItemTerminalOutcome.Faulted);
        correlation.CompletedAt.ShouldBe(completedAt);
    }

    [Fact]
    public void Revert_ShouldClearTerminalOutcome()
    {
        var correlation = CreateCorrelation("S");
        correlation.ApplyTerminalOutcome(SubItemTerminalOutcome.Canceled, DateTime.UtcNow);

        correlation.Revert();

        correlation.IsCompleted.ShouldBeFalse();
        correlation.CompletedAt.ShouldBeNull();
        correlation.TerminalOutcome.ShouldBeNull();
    }

    [Fact]
    public void SubFlowType_ShouldBeSubFlow_WhenCodeIsS()
    {
        // Arrange
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "state",
            Guid.NewGuid(),
            "S",
            "domain",
            "flow",
            null
        );

        // Act & Assert
        Assert.Equal(SubFlowType.SubFlow, correlation.SubFlowType);
        Assert.Equal("S", correlation.SubFlowType.Code);
    }

    [Fact]
    public void SubFlowType_ShouldBeSubProcess_WhenCodeIsP()
    {
        // Arrange
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "state",
            Guid.NewGuid(),
            "P",
            "domain",
            "flow",
            null
        );

        // Act & Assert
        Assert.Equal(SubFlowType.SubProcess, correlation.SubFlowType);
        Assert.Equal("P", correlation.SubFlowType.Code);
    }

    private static InstanceCorrelation CreateCorrelation(string typeCode) =>
        InstanceCorrelation.Create(
            Guid.NewGuid(), Guid.NewGuid(), "state", Guid.NewGuid(), typeCode,
            "domain", "flow", "1.0.0");
}
