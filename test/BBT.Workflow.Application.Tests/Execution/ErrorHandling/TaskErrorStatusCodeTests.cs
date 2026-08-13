using BBT.Aether.Results;
using BBT.Workflow.Execution.ErrorHandling;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.ErrorHandling;

/// <summary>
/// Unit tests for the semantic status carried on task errors. Without it every DirectTrigger
/// failure reaches the incident as the same task-key-derived code, so an error boundary cannot
/// express "retry on lock contention (409), fail fast on not-available (400)".
/// </summary>
public class TaskErrorStatusCodeTests
{
    private readonly ExecutionErrorFactory _factory = new(new ErrorNormalizer());

    [Theory]
    [InlineData(409)]
    [InlineData(400)]
    [InlineData(404)]
    public void CreateFromError_AetherPrefix_ResolvesStatusAndAppendsItToTheCode(int expectedStatus)
    {
        var error = expectedStatus switch
        {
            409 => Error.Conflict("Instance:100031", "Instance is busy"),
            404 => Error.NotFound("Instance:100001", "Instance not found"),
            _ => Error.Validation("Transition:100002", "Transition is not available in current state")
        };

        var executionError = _factory.CreateFromError(error, "trigger-transition", "DirectTrigger", 12);

        executionError.StatusCode.ShouldBe(expectedStatus);
        executionError.NormalizedError.StatusCode.ShouldBe(expectedStatus);
        executionError.NormalizedError.Code.ShouldBe($"Task:DirectTrigger:trigger-transition:{expectedStatus}");
    }

    [Fact]
    public void CreateFromError_AlreadyTaskScopedCode_IsPreservedInsteadOfReWrapped()
    {
        // The coordinator re-wraps an engine failure without knowing the task type. Rebuilding
        // would flatten the precise code into Task:Unknown:{key} and drop the status.
        var engineError = Error.Failure("Task:DirectTrigger:trigger-transition:409", "Instance is busy");

        var executionError = _factory.CreateFromError(engineError, "trigger-transition", "Unknown", 3);

        executionError.NormalizedError.Code.ShouldBe("Task:DirectTrigger:trigger-transition:409");
        executionError.NormalizedError.StatusCode.ShouldBe(409);
    }

    [Fact]
    public void CreateFromError_UnclassifiedError_KeepsTheStatuslessCode()
    {
        var executionError = _factory.CreateFromError(
            Error.Failure("Some:Code", "boom"), "my-task", "Script", 1);

        executionError.NormalizedError.StatusCode.ShouldBeNull();
        executionError.NormalizedError.Code.ShouldBe("Task:Script:my-task");
    }

    [Fact]
    public void ResolveStatusCode_BareNumericCode_IsUsedDirectly()
    {
        ErrorNormalizer.ResolveStatusCode(Error.Failure("503", "unavailable")).ShouldBe(503);
    }

    [Fact]
    public void Normalize_TransientStatus_IsFlaggedTransient()
    {
        // 503 retries; 409 is retryable by policy but is not classified transient — the boundary
        // rule decides, and it can now see the difference.
        new ErrorNormalizer().Normalize(Error.Transient("X", "unavailable")).IsTransient.ShouldBeTrue();
        new ErrorNormalizer().Normalize(Error.Conflict("Y", "busy")).IsTransient.ShouldBeFalse();
    }
}
