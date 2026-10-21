using System;
using System.Threading;
using System.Threading.Tasks;
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
    [Fact]
    public async Task Handle_ShouldBumpWorkflowGeneration()
    {
        var workflows = Substitute.For<ICacheSet<Definitions.Workflow>>();
        workflows.InvalidateAsync("core", "customer-onboarding", InstanceDataVersionComparer.LatestKeyword,
            Arg.Any<CancellationToken>()).Returns(BBT.Aether.Results.Result.Ok());
        var cacheContext = Substitute.For<IDomainCacheContext>();
        cacheContext.Workflows.Returns(workflows);
        var logger = Substitute.For<ILogger<InvalidateVersionPolicyJobHandler>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var handler = new InvalidateVersionPolicyJobHandler(cacheContext, logger);

        var result = await handler.HandleAsync(
            new InvalidateVersionPolicyJob("core", "customer-onboarding"),
            null!, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await workflows.Received(1).InvalidateAsync("core", "customer-onboarding",
            InstanceDataVersionComparer.LatestKeyword, Arg.Any<CancellationToken>());
        logger.ReceivedCalls().ShouldContain(c =>
            c.GetMethodInfo().Name == nameof(ILogger.Log) &&
            ((EventId)c.GetArguments()[1]!).Id == 20503);
    }
}
