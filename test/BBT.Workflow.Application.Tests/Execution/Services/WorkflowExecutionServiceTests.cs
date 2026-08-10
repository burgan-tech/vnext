using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Execution.Strategies;
using BBT.Workflow.Instances;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Services;

public sealed class WorkflowExecutionServiceTests
{
    [Fact]
    public async Task ExecuteTransitionCoreAsync_ShouldReturnTheOriginatingExecutionContextAndContinuationSnapshot()
    {
        var strategyFactory = Substitute.For<IExecutionStrategyFactory>();
        var strategy = Substitute.For<ITransitionStrategy>();
        var transitionRunner = Substitute.For<ITransitionRunner>();
        var executionContext = CreateExecutionContext();
        var postCommitJob = Substitute.For<IPostCommitJob>();
        var next = new NextTransitionRequest("next-transition", "automatic");
        executionContext.Directives.EnqueuePostCommit(postCommitJob);
        executionContext.Directives.RequestNextTransition(next);

        strategyFactory.Get(ExecMode.Sync).Returns(Result<ITransitionStrategy>.Ok(strategy));
        strategy.ExecuteAsync(Arg.Any<WorkflowExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<TransitionExecutionContext>.Ok(executionContext));

        var service = new WorkflowExecutionService(strategyFactory, transitionRunner);

        var result = await service.ExecuteTransitionCoreAsync(
            new WorkflowExecutionContext { Mode = ExecMode.Sync },
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var output = result.Value!;
        output.ExecutionContext.ShouldBeSameAs(executionContext);
        output.Continuations.Next.ShouldBeSameAs(next);
        output.Continuations.PostCommitJobs.Single().ShouldBeSameAs(postCommitJob);
    }

    private static TransitionExecutionContext CreateExecutionContext()
    {
        var instanceId = Guid.NewGuid();
        return new TransitionExecutionContext
        {
            Domain = "test-domain",
            InstanceId = instanceId,
            WorkflowKey = "test-workflow",
            TransitionKey = "test-transition",
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Instance = Instance.Create(instanceId, "test-workflow", "1.0.0"),
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }
}
