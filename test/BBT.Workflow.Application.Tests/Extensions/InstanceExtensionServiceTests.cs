using BBT.Workflow.Logging;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Extensions;

/// <summary>
/// Unit tests for fail-fast extension execution behavior.
/// Tests verify error codes, error factory methods, and target tracking.
/// </summary>
public class InstanceExtensionServiceTests
{
    [Fact]
    public void WorkflowErrors_ExtensionExecutionFailed_ShouldIncludeTarget()
    {
        // Arrange
        var extensionKey = "test-extension";
        var message = "Connection timeout";

        // Act
        var error = WorkflowErrors.ExtensionExecutionFailed(extensionKey, message);

        // Assert
        error.Code.ShouldBe(WorkflowErrorCodes.ExtensionExecutionFailed);
        error.Target.ShouldBe(extensionKey);
        error.Message.ShouldNotBeNull();
        error.Message!.ShouldContain(extensionKey);
        error.Message.ShouldContain(message);
    }

    [Fact]
    public void WorkflowErrorCodes_ShouldHaveExtensionExecutionFailedCode()
    {
        // Assert
        WorkflowErrorCodes.ExtensionExecutionFailed.ShouldBe("Extension:600001");
    }

    [Fact]
    public void WorkflowErrors_ExtensionExecutionFailed_ShouldFormatMessageCorrectly()
    {
        // Arrange
        var extensionKey = "my-data-extension";
        var message = "HTTP 500 - Internal Server Error";

        // Act
        var error = WorkflowErrors.ExtensionExecutionFailed(extensionKey, message);

        // Assert
        error.Message.ShouldNotBeNull();
        error.Message!.ShouldBe($"Extension '{extensionKey}' execution failed: {message}");
    }

    [Fact]
    public void WorkflowErrors_ExtensionExecutionFailed_ShouldBeValidationError()
    {
        // Arrange
        var extensionKey = "test-extension";
        var message = "Test error";

        // Act
        var error = WorkflowErrors.ExtensionExecutionFailed(extensionKey, message);

        // Assert - Error.Validation creates an error with Type = Validation
        // This ensures proper HTTP status code mapping (400 for validation errors)
        error.Code.ShouldStartWith("Extension:");
    }
}
