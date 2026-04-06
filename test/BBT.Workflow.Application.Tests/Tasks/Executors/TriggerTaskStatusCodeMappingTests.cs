using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Executors;

public class TriggerTaskStatusCodeMappingTests
{
    [Theory]
    [InlineData("conflict",     409)]
    [InlineData("notfound",     404)]
    [InlineData("validation",   400)]
    [InlineData("notsupported", 400)]
    [InlineData("unauthorized", 401)]
    [InlineData("forbidden",    403)]
    [InlineData("transient",    503)]
    [InlineData("dependency",   502)]
    [InlineData("failure",      500)]
    [InlineData("unknown",      500)]
    public void MapErrorToStatusCode_ShouldReturnExpectedStatusCode(string prefix, int expectedStatusCode)
    {
        var error = new Error(prefix, "test-code", "test message");

        var actual = TestTriggerExecutor.MapCode(error);

        actual.ShouldBe(expectedStatusCode);
    }

    [Fact]
    public void MapErrorToStatusCode_ConflictError_Returns409()
    {
        var error = Error.Conflict("Workflow.DuplicateKey", "An active instance already exists");
        TestTriggerExecutor.MapCode(error).ShouldBe(409);
    }

    [Fact]
    public void MapErrorToStatusCode_NotFoundError_Returns404()
    {
        var error = Error.NotFound("Workflow.InstanceNotFound", "Instance not found");
        TestTriggerExecutor.MapCode(error).ShouldBe(404);
    }

    [Fact]
    public void MapErrorToStatusCode_ValidationError_Returns400()
    {
        var error = Error.Validation("Workflow.InvalidInput", "Invalid input");
        TestTriggerExecutor.MapCode(error).ShouldBe(400);
    }

    [Fact]
    public void MapErrorToStatusCode_UnauthorizedError_Returns401()
    {
        var error = Error.Unauthorized();
        TestTriggerExecutor.MapCode(error).ShouldBe(401);
    }

    [Fact]
    public void MapErrorToStatusCode_ForbiddenError_Returns403()
    {
        var error = Error.Forbidden();
        TestTriggerExecutor.MapCode(error).ShouldBe(403);
    }

    [Fact]
    public void MapErrorToStatusCode_TransientError_Returns503()
    {
        var error = Error.Transient("Workflow.Timeout", "Operation timed out");
        TestTriggerExecutor.MapCode(error).ShouldBe(503);
    }

    [Fact]
    public void MapErrorToStatusCode_DependencyError_Returns502()
    {
        var error = Error.Dependency("Workflow.DatabaseFailure", "Database unavailable");
        TestTriggerExecutor.MapCode(error).ShouldBe(502);
    }

    [Fact]
    public void MapErrorToStatusCode_FailureError_Returns500()
    {
        var error = Error.Failure("Workflow.Unexpected", "Unexpected error");
        TestTriggerExecutor.MapCode(error).ShouldBe(500);
    }

    /// <summary>
    /// Minimal subclass to expose the protected static MapErrorToStatusCode helper for testing.
    /// No instantiation is needed — only the static accessor is called.
    /// </summary>
    private sealed class TestTriggerExecutor(
        IScriptEngine scriptEngine,
        IRuntimeInfoProvider runtimeInfoProvider,
        IRemoteInvokerService remoteInvoker,
        ILogger logger)
        : TriggerTaskExecutorBase<StartTask>(scriptEngine, runtimeInfoProvider, remoteInvoker, logger)
    {
        public override TaskType TaskType => TaskType.StartTrigger;

        protected override string GetTargetDomain(StartTask task) => string.Empty;

        protected override Task<Result<TaskInvocationResult>> InvokeAsync(
            StartTask task,
            TaskExecutorContext context,
            CancellationToken cancellationToken) => throw new NotImplementedException();

        public static int MapCode(Error error) => MapErrorToStatusCode(error);
    }
}
