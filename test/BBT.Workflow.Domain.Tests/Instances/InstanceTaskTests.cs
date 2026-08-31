using System;
using BBT.Workflow.Definitions;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for InstanceTask
/// </summary>
public class InstanceTaskTests : DomainTestBase<DomainEntryPoint>
{
    [Fact]
    public void Constructor_ShouldInitializeAllProperties()
    {
        // Arrange
        var id = Guid.NewGuid();
        var transitionId = Guid.NewGuid();
        var taskId = "test-task";

        // Act
        var instanceTask = new InstanceTask(id, transitionId, taskId, TaskTrigger.OnExecute, 1);

        // Assert
        Assert.Equal(id, instanceTask.Id);
        Assert.Equal(transitionId, instanceTask.TransitionId);
        Assert.Equal(taskId, instanceTask.TaskId);
        Assert.Equal(TaskStatus.Waiting, instanceTask.Status);
        Assert.NotNull(instanceTask.Request);
        Assert.NotNull(instanceTask.Response);
        Assert.Null(instanceTask.FinishedAt);
        Assert.Null(instanceTask.Duration);
        Assert.Null(instanceTask.FaultedTaskId);
    }

    [Fact]
    public void Constructor_ShouldSetStartedAtToCurrentTime()
    {
        // Arrange
        var before = DateTime.UtcNow.AddSeconds(-1);

        // Act
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);
        var after = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.True(instanceTask.StartedAt >= before && instanceTask.StartedAt <= after);
    }

    [Fact]
    public void Constructor_ShouldInitializeRequestAndResponseAsEmpty()
    {
        // Arrange & Act
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);

        // Assert
        Assert.NotNull(instanceTask.Request);
        Assert.NotNull(instanceTask.Response);
        // JsonData boş nesne olarak başlatılır
        Assert.Equal("{}", instanceTask.Request.Json);
        Assert.Equal("{}", instanceTask.Response.Json);
    }

    [Fact]
    public void Completed_ShouldSetStatusToCompleted()
    {
        // Arrange
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);
        var response = JsonData.CreateFrom("{\"result\":\"success\"}");

        // Act
        instanceTask.Completed(response);

        // Assert
        Assert.Equal(TaskStatus.Completed, instanceTask.Status);
    }

    [Fact]
    public void Completed_ShouldSetResponseData()
    {
        // Arrange
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);
        var response = JsonData.CreateFrom("{\"result\":\"success\"}");

        // Act
        instanceTask.Completed(response);

        // Assert
        Assert.NotNull(instanceTask.Response);
        Assert.Equal(response.Json, instanceTask.Response.Json);
    }

    [Fact]
    public void Completed_ShouldSetFinishedAtToCurrentTime()
    {
        // Arrange
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);
        var response = JsonData.CreateFrom("{}");
        var before = DateTime.UtcNow.AddSeconds(-1);

        // Act
        instanceTask.Completed(response);
        var after = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.NotNull(instanceTask.FinishedAt);
        Assert.True(instanceTask.FinishedAt >= before && instanceTask.FinishedAt <= after);
    }

    [Fact]
    public void Faulted_ShouldSetStatusToFaulted()
    {
        // Arrange
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);
        var reason = "Task execution failed";

        // Act
        instanceTask.Faulted(reason);

        // Assert
        Assert.Equal(TaskStatus.Faulted, instanceTask.Status);
    }

    [Fact]
    public void Faulted_ShouldSetResponseWithErrorMessage()
    {
        // Arrange
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);
        var reason = "Task execution failed";

        // Act
        instanceTask.Faulted(reason);

        // Assert
        Assert.NotNull(instanceTask.Response);
        Assert.Contains("error", instanceTask.Response.Json);
        Assert.Contains(reason, instanceTask.Response.Json);
    }

    [Fact]
    public void Faulted_ShouldSetFinishedAtToCurrentTime()
    {
        // Arrange
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);
        var before = DateTime.UtcNow.AddSeconds(-1);

        // Act
        instanceTask.Faulted("error");
        var after = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.NotNull(instanceTask.FinishedAt);
        Assert.True(instanceTask.FinishedAt >= before && instanceTask.FinishedAt <= after);
    }

    [Fact]
    public void Status_ShouldBeWaiting_Initially()
    {
        // Arrange & Act
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);

        // Assert
        Assert.Equal(TaskStatus.Waiting, instanceTask.Status);
    }

    [Fact]
    public void Duration_ShouldBeNull_BeforeCompletion()
    {
        // Arrange & Act
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);

        // Assert
        Assert.Null(instanceTask.Duration);
    }

    [Fact]
    public void FinishedAt_ShouldBeNull_BeforeCompletion()
    {
        // Arrange & Act
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);

        // Assert
        Assert.Null(instanceTask.FinishedAt);
    }

    [Fact]
    public void FaultedTaskId_ShouldBeNull_Initially()
    {
        // Arrange & Act
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);

        // Assert
        Assert.Null(instanceTask.FaultedTaskId);
    }

    [Fact]
    public void Completed_WithComplexResponse_ShouldStoreCorrectly()
    {
        // Arrange
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);
        var response = JsonData.CreateFrom(@"
        {
            ""status"": ""success"",
            ""data"": {
                ""items"": [1, 2, 3],
                ""count"": 3
            }
        }");

        // Act
        instanceTask.Completed(response);

        // Assert
        Assert.Contains("status", instanceTask.Response.Json);
        Assert.Contains("data", instanceTask.Response.Json);
        Assert.Contains("items", instanceTask.Response.Json);
    }

    [Fact]
    public void Faulted_WithSpecialCharacters_ShouldEscapeCorrectly()
    {
        // Arrange
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);
        var reason = "Task failed: \"Unexpected error\" with 'quotes'";

        // Act
        instanceTask.Faulted(reason);

        // Assert
        Assert.NotNull(instanceTask.Response);
        // The response should contain the error and be valid JSON
        Assert.Contains("error", instanceTask.Response.Json);
    }

    [Fact]
    public void Completed_ShouldBeIdempotent()
    {
        // Arrange
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);
        var response1 = JsonData.CreateFrom("{\"result\":\"first\"}");
        var response2 = JsonData.CreateFrom("{\"result\":\"second\"}");

        // Act
        instanceTask.Completed(response1);
        var firstFinishedAt = instanceTask.FinishedAt;

        System.Threading.Thread.Sleep(10);

        instanceTask.Completed(response2);
        var secondFinishedAt = instanceTask.FinishedAt;

        // Assert
        Assert.Equal(TaskStatus.Completed, instanceTask.Status);
        // Response gets updated
        Assert.Contains("second", instanceTask.Response.Json);
        // FinishedAt gets updated
        Assert.NotEqual(firstFinishedAt, secondFinishedAt);
    }

    [Fact]
    public void Faulted_ShouldBeIdempotent()
    {
        // Arrange
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);

        // Act
        instanceTask.Faulted("error 1");
        var firstFinishedAt = instanceTask.FinishedAt;

        System.Threading.Thread.Sleep(10);

        instanceTask.Faulted("error 2");
        var secondFinishedAt = instanceTask.FinishedAt;

        // Assert
        Assert.Equal(TaskStatus.Faulted, instanceTask.Status);
        // Response gets updated
        Assert.Contains("error 2", instanceTask.Response.Json);
        // FinishedAt gets updated
        Assert.NotEqual(firstFinishedAt, secondFinishedAt);
    }

    [Fact]
    public void Completed_AfterFaulted_ShouldChangeStatus()
    {
        // Arrange
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);

        // Act
        instanceTask.Faulted("error");
        Assert.Equal(TaskStatus.Faulted, instanceTask.Status);

        instanceTask.Completed(JsonData.CreateFrom("{\"result\":\"success\"}"));

        // Assert
        Assert.Equal(TaskStatus.Completed, instanceTask.Status);
        Assert.Contains("success", instanceTask.Response.Json);
    }

    [Fact]
    public void Faulted_AfterCompleted_ShouldChangeStatus()
    {
        // Arrange
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);

        // Act
        instanceTask.Completed(JsonData.CreateFrom("{\"result\":\"success\"}"));
        Assert.Equal(TaskStatus.Completed, instanceTask.Status);

        instanceTask.Faulted("error");

        // Assert
        Assert.Equal(TaskStatus.Faulted, instanceTask.Status);
        Assert.Contains("error", instanceTask.Response.Json);
    }

    [Fact]
    public void Completed_WithEmptyResponse_ShouldWork()
    {
        // Arrange
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);
        var response = JsonData.CreateFrom("{}");

        // Act
        instanceTask.Completed(response);

        // Assert
        Assert.Equal(TaskStatus.Completed, instanceTask.Status);
        Assert.NotNull(instanceTask.Response);
    }

    [Fact]
    public void Faulted_WithEmptyReason_ShouldWork()
    {
        // Arrange
        var instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "task-id", TaskTrigger.OnExecute, 1);

        // Act
        instanceTask.Faulted(string.Empty);

        // Assert
        Assert.Equal(TaskStatus.Faulted, instanceTask.Status);
        Assert.NotNull(instanceTask.Response);
        Assert.Contains("error", instanceTask.Response.Json);
    }
}

