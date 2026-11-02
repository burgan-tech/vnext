using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Execution.PostCommit.Handlers;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.PostCommit.Handlers;

public class InvalidateVersionPolicyJobHandlerTests
{
    private const string Domain = CacheSetTestHarness.TestDomain;
    private const string Flow = CacheSetTestHarness.TestKey;

    private static (InvalidateVersionPolicyJobHandler Handler, ICacheSet<Definitions.Workflow> Workflows,
        ILogger<InvalidateVersionPolicyJobHandler> Logger) Create()
    {
        var workflows = Substitute.For<ICacheSet<Definitions.Workflow>>();
        var cacheContext = Substitute.For<IDomainCacheContext>();
        cacheContext.Workflows.Returns(workflows);
        var logger = Substitute.For<ILogger<InvalidateVersionPolicyJobHandler>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        return (new InvalidateVersionPolicyJobHandler(cacheContext, logger), workflows, logger);
    }

    private static bool Logged(ILogger logger, int eventId, LogLevel level) =>
        logger.ReceivedCalls().Any(c =>
            c.GetMethodInfo().Name == nameof(ILogger.Log) &&
            (LogLevel)c.GetArguments()[0]! == level &&
            ((EventId)c.GetArguments()[1]!).Id == eventId);

    /// <summary>
    /// The Workflows set delegates to a real CacheSet (real ComponentGenerationProvider), so the
    /// generation token genuinely changes. The harness set is typed for View, but InvalidateAsync is
    /// the non-generic ICacheSet member and keys only on (type, domain, key).
    /// </summary>
    [Fact]
    public async Task Handle_ShouldBumpWorkflowGeneration()
    {
        var harness = new CacheSetTestHarness();
        var before = await harness.CurrentGenerationAsync();
        var (handler, workflows, logger) = Create();
        workflows.InvalidateAsync(Domain, Flow, InstanceDataVersionComparer.LatestKeyword, Arg.Any<CancellationToken>())
            .Returns(ci => harness.Sut.InvalidateAsync(
                ci.ArgAt<string>(0), ci.ArgAt<string>(1), ci.ArgAt<string>(2), ci.ArgAt<CancellationToken>(3)));

        var result = await handler.HandleAsync(new InvalidateVersionPolicyJob(Domain, Flow), null!, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var after = await harness.CurrentGenerationAsync();
        after.ShouldNotBe(before);
        Logged(logger, 20503, LogLevel.Information).ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_ShouldSucceed_AndWarn_WhenInvalidateFails()
    {
        var (handler, workflows, logger) = Create();
        workflows.InvalidateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail(Error.Failure("Cache:Down", "down")));

        var result = await handler.HandleAsync(new InvalidateVersionPolicyJob(Domain, Flow), null!, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        Logged(logger, 20504, LogLevel.Warning).ShouldBeTrue();
        Logged(logger, 20503, LogLevel.Information).ShouldBeFalse();
    }

    [Fact]
    public async Task Handle_ShouldSucceed_AndWarn_WhenInvalidateThrows()
    {
        var (handler, workflows, logger) = Create();
        workflows.InvalidateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result>>(_ => throw new InvalidOperationException("boom"));

        var result = await handler.HandleAsync(new InvalidateVersionPolicyJob(Domain, Flow), null!, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        Logged(logger, 20504, LogLevel.Warning).ShouldBeTrue();
    }

    [Fact]
    public async Task Handle_ShouldPropagateCancellation()
    {
        var (handler, workflows, _) = Create();
        workflows.InvalidateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result>>(_ => throw new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() =>
            handler.HandleAsync(new InvalidateVersionPolicyJob(Domain, Flow), null!, CancellationToken.None));
    }
}
