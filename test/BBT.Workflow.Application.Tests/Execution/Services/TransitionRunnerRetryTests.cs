using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Services;

public class TransitionRunnerRetryTests
{
    private static TransitionRunner CreateRunner(
        IDbTransientErrorClassifier classifier,
        DbRetryOptions? options = null,
        Func<WorkflowExecutionContext, CancellationToken, Task<Result<TransitionCoreOutput>>>? scopeDelegate = null)
    {
        var opts = options ?? new DbRetryOptions
        {
            MaxRetryAttempts = 3,
            BaseDelayMilliseconds = 1,   // tiny delays for test speed
            MaxDelayMilliseconds = 10,
            UseJitter = false
        };

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var logger = NullLogger<TransitionRunner>.Instance;
        var retryOptions = Options.Create(opts);

        return scopeDelegate != null
            ? new TransitionRunner(scopeFactory, logger, retryOptions, classifier) { ScopeDelegate = scopeDelegate }
            : new TransitionRunner(scopeFactory, logger, retryOptions, classifier);
    }

    private static WorkflowExecutionContext CreateContext() =>
        new WorkflowExecutionContext
        {
            Domain = "test-domain",
            WorkflowKey = "test-flow",
            TransitionKey = "test-transition"
        };

    private static TransitionCoreOutput CreateFakeOutput() =>
        new TransitionCoreOutput(
            new TransitionOutput { Id = Guid.NewGuid() },
            Array.Empty<BBT.Aether.Events.DomainEventEnvelope>(),
            ContinuationSet.Empty);

    [Fact]
    public async Task RunAsync_WhenTransientExceptionOnFirstAttemptThenSuccess_ShouldRetryAndSucceed()
    {
        // Arrange
        var classifier = Substitute.For<IDbTransientErrorClassifier>();
        classifier.IsRetriableTransient(Arg.Any<Exception>()).Returns(true);

        var context = CreateContext();
        var fakeOutput = CreateFakeOutput();
        var callCount = 0;

        var runner = CreateRunner(classifier, scopeDelegate: (ctx, ct) =>
        {
            callCount++;
            if (callCount == 1)
                throw new SocketException();
            return Task.FromResult(Result<TransitionCoreOutput>.Ok(fakeOutput));
        });

        // Act
        var result = await runner.RunAsync(context);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        callCount.ShouldBe(2); // 1 failure + 1 success
    }

    [Fact]
    public async Task RunAsync_WhenPoolExhaustionException_ShouldNotRetry()
    {
        // Arrange
        var classifier = Substitute.For<IDbTransientErrorClassifier>();
        // Pool exhaustion returns false — never retry
        classifier.IsRetriableTransient(Arg.Any<Exception>()).Returns(false);

        var context = CreateContext();
        var callCount = 0;
        var poolException = new InvalidOperationException("pool has been exhausted");

        var runner = CreateRunner(classifier, scopeDelegate: (ctx, ct) =>
        {
            callCount++;
            throw poolException;
        });

        // Act
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => runner.RunAsync(context));

        // Assert
        callCount.ShouldBe(1); // never retried
        ex.ShouldBeSameAs(poolException);
    }

    [Fact]
    public async Task RunAsync_WhenTransientExceptionExceedsMaxRetries_ShouldThrow()
    {
        // Arrange
        var classifier = Substitute.For<IDbTransientErrorClassifier>();
        classifier.IsRetriableTransient(Arg.Any<Exception>()).Returns(true);

        var opts = new DbRetryOptions
        {
            MaxRetryAttempts = 2,
            BaseDelayMilliseconds = 1,
            MaxDelayMilliseconds = 5,
            UseJitter = false
        };

        var context = CreateContext();
        var callCount = 0;
        var transientException = new SocketException();

        var runner = CreateRunner(classifier, opts, scopeDelegate: (ctx, ct) =>
        {
            callCount++;
            throw transientException;
        });

        // Act & Assert: Polly exhausts all retries and rethrows
        await Should.ThrowAsync<SocketException>(() => runner.RunAsync(context));

        // MaxRetryAttempts = 2 means initial attempt + 2 retries = 3 total calls
        callCount.ShouldBe(3);
    }

    [Fact]
    public async Task RunAsync_WhenScopeReturnsFailResult_ShouldReturnFail()
    {
        // Arrange
        var classifier = Substitute.For<IDbTransientErrorClassifier>();
        var context = CreateContext();
        var error = new BBT.Aether.Results.Error("TEST_ERROR", "Something went wrong");

        var runner = CreateRunner(classifier, scopeDelegate: (ctx, ct) =>
            Task.FromResult(Result<TransitionCoreOutput>.Fail(error)));

        // Act
        var result = await runner.RunAsync(context);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldNotBe(default);
    }
}
