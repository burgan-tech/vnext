using System;
using System.Collections.Generic;
using BBT.Aether.Results;
using BBT.Workflow.Execution.PostCommit;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.PostCommit;

/// <summary>
/// Unit tests for DefaultPostCommitFailurePolicy.
/// Tests error classification and instance faulting decisions.
/// </summary>
public class DefaultPostCommitFailurePolicyTests
{
    private readonly DefaultPostCommitFailurePolicy _policy;

    public DefaultPostCommitFailurePolicyTests()
    {
        _policy = new DefaultPostCommitFailurePolicy();
    }

    #region Client Errors (Should NOT Fault Instance)

    [Fact]
    public void Decide_WhenValidationError_ShouldNotFaultInstance()
    {
        // Arrange
        var error = Error.Validation("Transition:100020", "Transition not available in current state");
        var context = CreateContext(error);

        // Act
        var decision = _policy.Decide(context);

        // Assert
        decision.ShouldContinue.ShouldBeFalse();
        decision.ShouldMarkInstanceFaulted.ShouldBeFalse();
        decision.FaultErrorCode.ShouldBe(string.Empty);
        decision.FaultErrorMessage.ShouldBeNull();
    }

    [Fact]
    public void Decide_WhenNotFoundError_ShouldNotFaultInstance()
    {
        // Arrange
        var error = Error.NotFound("Instance:404", "Instance not found");
        var context = CreateContext(error);

        // Act
        var decision = _policy.Decide(context);

        // Assert
        decision.ShouldMarkInstanceFaulted.ShouldBeFalse();
    }

    [Fact]
    public void Decide_WhenConflictError_ShouldNotFaultInstance()
    {
        // Arrange
        var error = Error.Conflict("Instance:409", "Instance already exists");
        var context = CreateContext(error);

        // Act
        var decision = _policy.Decide(context);

        // Assert
        decision.ShouldMarkInstanceFaulted.ShouldBeFalse();
    }

    [Fact]
    public void Decide_WhenUnauthorizedError_ShouldNotFaultInstance()
    {
        // Arrange
        var error = Error.Unauthorized("Auth:401", "Unauthorized access");
        var context = CreateContext(error);

        // Act
        var decision = _policy.Decide(context);

        // Assert
        decision.ShouldMarkInstanceFaulted.ShouldBeFalse();
    }

    [Fact]
    public void Decide_WhenForbiddenError_ShouldNotFaultInstance()
    {
        // Arrange
        var error = Error.Forbidden("Auth:403", "Forbidden access");
        var context = CreateContext(error);

        // Act
        var decision = _policy.Decide(context);

        // Assert
        decision.ShouldMarkInstanceFaulted.ShouldBeFalse();
    }

    #endregion

    #region System Errors (SHOULD Fault Instance)

    [Fact]
    public void Decide_WhenDependencyError_ShouldFaultInstance()
    {
        // Arrange
        var error = Error.Dependency("remote_service_error", "Remote API service error");
        var context = CreateContext(error);

        // Act
        var decision = _policy.Decide(context);

        // Assert
        decision.ShouldContinue.ShouldBeFalse();
        decision.ShouldMarkInstanceFaulted.ShouldBeTrue();
        decision.FaultErrorCode.ShouldBe("remote_service_error");
        decision.FaultErrorMessage.ShouldBe("Remote API service error");
    }

    [Fact]
    public void Decide_WhenTransientError_ShouldFaultInstance()
    {
        // Arrange
        var error = Error.Transient("network_timeout", "Network timeout occurred");
        var context = CreateContext(error);

        // Act
        var decision = _policy.Decide(context);

        // Assert
        decision.ShouldMarkInstanceFaulted.ShouldBeTrue();
        decision.FaultErrorCode.ShouldBe("network_timeout");
    }

    [Fact]
    public void Decide_WhenFailureError_ShouldFaultInstance()
    {
        // Arrange
        var error = Error.Failure("system_error", "System error occurred");
        var context = CreateContext(error);

        // Act
        var decision = _policy.Decide(context);

        // Assert
        decision.ShouldMarkInstanceFaulted.ShouldBeTrue();
    }

    #endregion

    #region Decision Properties

    [Fact]
    public void Decide_ForClientErrors_ShouldNotContinue()
    {
        // Arrange
        var error = Error.Validation("test", "test error");
        var context = CreateContext(error);

        // Act
        var decision = _policy.Decide(context);

        // Assert
        decision.ShouldContinue.ShouldBeFalse();
    }

    [Fact]
    public void Decide_ForSystemErrors_ShouldNotContinue()
    {
        // Arrange
        var error = Error.Dependency("test", "test error");
        var context = CreateContext(error);

        // Act
        var decision = _policy.Decide(context);

        // Assert
        decision.ShouldContinue.ShouldBeFalse();
    }

    #endregion

    private static PostCommitFailureContext CreateContext(Error error)
    {
        var job = new ForwardToSubflowJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "test-transition",
            "test-domain",
            "test-workflow",
            "1.0.0",
            "test-key",
            null,
            null,
            new Dictionary<string, string?>(),
            new Dictionary<string, string?>()
        );

        return new PostCommitFailureContext(
            Job: job,
            Error: error,
            JobIndex: 0,
            TotalJobs: 1
        );
    }
}
