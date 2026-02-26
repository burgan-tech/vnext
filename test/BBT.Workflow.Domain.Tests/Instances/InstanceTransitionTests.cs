using System;
using BBT.Workflow.Definitions;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for InstanceTransition
/// </summary>
public class InstanceTransitionTests : DomainTestBase<DomainEntryPoint>
{
    [Fact]
    public void Create_ShouldInitializeAllProperties()
    {
        // Arrange
        var id = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var transitionId = "test-transition";
        var fromState = "from-state";
        var body = JsonData.CreateFrom("{\"key\":\"value\"}");
        var header = JsonData.CreateFrom("{\"header\":\"data\"}");

        // Act
        var instanceTransition = InstanceTransition.Create(
            id,
            instanceId,
            transitionId,
            fromState,
            TriggerType.Manual,
            body,
            header
        );

        // Assert
        Assert.Equal(id, instanceTransition.Id);
        Assert.Equal(instanceId, instanceTransition.InstanceId);
        Assert.Equal(transitionId, instanceTransition.TransitionId);
        Assert.Equal(fromState, instanceTransition.FromState);
        Assert.Equal(body, instanceTransition.Body);
        Assert.Equal(header, instanceTransition.Header);
        Assert.Equal(TriggerType.Manual, instanceTransition.TriggerType);
        Assert.Null(instanceTransition.ToState);
        Assert.Null(instanceTransition.FinishedAt);
        Assert.Null(instanceTransition.Duration);
    }

    [Fact]
    public void Create_ShouldSetStartedAtToCurrentTime()
    {
        // Arrange
        var before = DateTime.UtcNow.AddSeconds(-1);

        // Act
        var instanceTransition = InstanceTransition.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "transition",
            "state",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}")
        );
        var after = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.True(instanceTransition.StartedAt >= before && instanceTransition.StartedAt <= after);
    }

    [Fact]
    public void Completed_ShouldSetToStateAndFinishedAt()
    {
        // Arrange
        var instanceTransition = InstanceTransition.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "transition",
            "from-state",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}")
        );
        var toState = "to-state";
        var before = DateTime.UtcNow.AddSeconds(-1);

        // Act
        instanceTransition.Completed(toState);
        var after = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.Equal(toState, instanceTransition.ToState);
        Assert.NotNull(instanceTransition.FinishedAt);
        Assert.True(instanceTransition.FinishedAt >= before && instanceTransition.FinishedAt <= after);
    }

    [Fact]
    public void Completed_ShouldCalculateDuration()
    {
        // Arrange
        var instanceTransition = InstanceTransition.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "transition",
            "from-state",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}")
        );
        var toState = "to-state";

        // Act
        System.Threading.Thread.Sleep(100); // Wait a bit to ensure duration is measurable
        instanceTransition.Completed(toState);

        // Assert
        Assert.NotNull(instanceTransition.Duration);
        Assert.True(instanceTransition.Duration.Value.TotalMilliseconds >= 100);
    }

    [Fact]
    public void Completed_ShouldSetDurationBasedOnStartedAt()
    {
        // Arrange
        var instanceTransition = InstanceTransition.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "transition",
            "from-state",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}")
        );
        var startedAt = instanceTransition.StartedAt;

        // Act
        instanceTransition.Completed("to-state");

        // Assert
        Assert.NotNull(instanceTransition.Duration);
        Assert.NotNull(instanceTransition.FinishedAt);
        Assert.Equal(instanceTransition.FinishedAt.Value - startedAt, instanceTransition.Duration.Value);
    }

    [Fact]
    public void Body_ShouldBeAccessible()
    {
        // Arrange
        var bodyData = JsonData.CreateFrom("{\"key\":\"value\",\"number\":42}");
        var instanceTransition = InstanceTransition.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "transition",
            "state",
            TriggerType.Manual,
            bodyData,
            JsonData.CreateFrom("{}")
        );

        // Act
        var body = instanceTransition.Body;

        // Assert
        Assert.NotNull(body);
        Assert.Equal(bodyData.Json, body.Json);
    }

    [Fact]
    public void Header_ShouldBeAccessible()
    {
        // Arrange
        var headerData = JsonData.CreateFrom("{\"authorization\":\"Bearer token\"}");
        var instanceTransition = InstanceTransition.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "transition",
            "state",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            headerData
        );

        // Act
        var header = instanceTransition.Header;

        // Assert
        Assert.NotNull(header);
        Assert.Equal(headerData.Json, header.Json);
    }

    [Fact]
    public void Completed_ShouldBeIdempotent()
    {
        // Arrange
        var instanceTransition = InstanceTransition.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "transition",
            "from-state",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}")
        );

        // Act
        instanceTransition.Completed("state-1");
        var firstFinishedAt = instanceTransition.FinishedAt;
        var firstDuration = instanceTransition.Duration;

        System.Threading.Thread.Sleep(10);

        instanceTransition.Completed("state-2");
        var secondFinishedAt = instanceTransition.FinishedAt;
        var secondDuration = instanceTransition.Duration;
        var secondToState = instanceTransition.ToState;

        // Assert
        // Properties get updated on subsequent calls
        Assert.NotEqual(firstFinishedAt, secondFinishedAt);
        Assert.NotEqual(firstDuration, secondDuration);
        Assert.Equal("state-2", secondToState);
    }

    [Fact]
    public void Create_ShouldHandleEmptyJsonData()
    {
        // Arrange
        var body = JsonData.CreateFrom("{}");
        var header = JsonData.CreateFrom("{}");

        // Act
        var instanceTransition = InstanceTransition.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "transition",
            "state",
            TriggerType.Manual,
            body,
            header
        );

        // Assert
        Assert.NotNull(instanceTransition.Body);
        Assert.NotNull(instanceTransition.Header);
        Assert.Equal("{}", instanceTransition.Body.Json);
        Assert.Equal("{}", instanceTransition.Header.Json);
    }

    [Fact]
    public void Create_ShouldHandleComplexJsonData()
    {
        // Arrange
        var body = JsonData.CreateFrom("{\"user\":{\"id\":1,\"name\":\"Test\"},\"items\":[1,2,3]}");
        var header = JsonData.CreateFrom("{\"trace-id\":\"12345\",\"span-id\":\"67890\"}");

        // Act
        var instanceTransition = InstanceTransition.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "transition",
            "state",
            TriggerType.Manual,
            body,
            header
        );

        // Assert
        Assert.NotNull(instanceTransition.Body);
        Assert.NotNull(instanceTransition.Header);
        Assert.Contains("user", instanceTransition.Body.Json);
        Assert.Contains("trace-id", instanceTransition.Header.Json);
    }

    [Fact]
    public void Duration_ShouldBeNull_BeforeCompletion()
    {
        // Arrange
        var instanceTransition = InstanceTransition.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "transition",
            "state",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}")
        );

        // Assert
        Assert.Null(instanceTransition.Duration);
        Assert.Null(instanceTransition.FinishedAt);
        Assert.Null(instanceTransition.ToState);
    }

    [Fact]
    public void Create_WithMultipleInstances_ShouldHaveUniqueStartedAt()
    {
        // Arrange & Act
        var transition1 = InstanceTransition.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "transition1",
            "state",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}")
        );

        System.Threading.Thread.Sleep(10);

        var transition2 = InstanceTransition.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "transition2",
            "state",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}")
        );

        // Assert
        Assert.NotEqual(transition1.StartedAt, transition2.StartedAt);
    }
}

