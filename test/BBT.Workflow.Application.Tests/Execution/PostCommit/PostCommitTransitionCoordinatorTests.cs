using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Continuations;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Instances;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.PostCommit;

public sealed class PostCommitTransitionCoordinatorTests
{
    private readonly IPostCommitExecutor _executor = Substitute.For<IPostCommitExecutor>();

    [Fact]
    public async Task CoordinateAsync_WhenThereAreNoJobs_ShouldReturnSourceWithoutNextContext()
    {
        var source = CreateContext();
        var coordinator = CreateCoordinator(new InlineContinuationStrategy());

        var result = await coordinator.CoordinateAsync(source, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.SourceContext.ShouldBeSameAs(source);
        result.Value.NextContext.ShouldBeNull();
        result.Value.FaultRequest.ShouldBeNull();
        result.Value.Error.ShouldBeNull();
        await _executor.DidNotReceiveWithAnyArgs()
            .ExecuteAsync(default!, default!, default);
    }

    [Fact]
    public async Task CoordinateAsync_WhenJobHandsOffToChild_ShouldNeverDispatchOuterContinuation()
    {
        var source = CreateContext();
        source.Directives.EnqueuePostCommit(new TestContinuationJob(PostCommitContinuationBehavior.HandoffToChild));
        source.Directives.RequestNextTransition(new NextTransitionRequest("stale-parent-next", "automatic"));
        _executor.ExecuteAsync(
                Arg.Any<IReadOnlyList<IPostCommitJob>>(),
                source,
                Arg.Any<CancellationToken>())
            .Returns(_ => PostCommitResult.Ok());
        var strategy = new RecordingInlineStrategy();
        var coordinator = CreateCoordinator(strategy);

        var result = await coordinator.CoordinateAsync(source, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.SourceContext.ShouldBeSameAs(source);
        result.Value.NextContext.ShouldBeNull();
        strategy.DispatchCount.ShouldBe(0);
        source.Directives.PostCommitJobs.ShouldBeEmpty();

        await coordinator.CoordinateAsync(source, CancellationToken.None);
        await _executor.Received(1).ExecuteAsync(
            Arg.Is<IReadOnlyList<IPostCommitJob>>(jobs => jobs.Count == 1),
            source,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CoordinateAsync_WhenJobContinuesParent_ShouldDispatchInlineFromIdentityOnlyAfterAllJobsSucceed()
    {
        var source = CreateContext();
        source.Directives.EnqueuePostCommit(new TestContinuationJob(PostCommitContinuationBehavior.ContinueParent));
        source.Directives.RequestNextTransition(new NextTransitionRequest("fresh-parent-next", "automatic"));
        var jobsSucceeded = false;
        _executor.ExecuteAsync(
                Arg.Any<IReadOnlyList<IPostCommitJob>>(),
                source,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                jobsSucceeded = true;
                source.Instance = null!; // The pre-commit aggregate is stale after remote work begins.
                return PostCommitResult.Ok();
            });
        var strategy = new RecordingInlineStrategy(() => jobsSucceeded.ShouldBeTrue());
        var coordinator = CreateCoordinator(strategy);

        var result = await coordinator.CoordinateAsync(source, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.NextContext.ShouldNotBeNull();
        result.Value.NextContext!.ShouldNotBeSameAs(source);
        result.Value.NextContext.TransitionKey.ShouldBe("fresh-parent-next");
        strategy.DispatchCount.ShouldBe(1);
        source.Directives.PostCommitJobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task CoordinateAsync_WhenExecutorFailsWithoutFaultRequest_ShouldReturnFailedResult()
    {
        var source = CreateContext();
        source.Directives.EnqueuePostCommit(new TestContinuationJob(PostCommitContinuationBehavior.ContinueParent));
        source.Directives.RequestNextTransition(new NextTransitionRequest("must-not-dispatch", "automatic"));
        var error = Error.Failure("PostCommit:Failed", "job failed");
        _executor.ExecuteAsync(
                Arg.Any<IReadOnlyList<IPostCommitJob>>(),
                source,
                Arg.Any<CancellationToken>())
            .Returns(PostCommitResult.Fail(error));
        var strategy = new RecordingInlineStrategy();
        var coordinator = CreateCoordinator(strategy);

        var result = await coordinator.CoordinateAsync(source, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(error);
        strategy.DispatchCount.ShouldBe(0);
        source.Directives.PostCommitJobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task CoordinateAsync_WhenExecutorRequestsFault_ShouldCarryFreshStateRecoveryDecision()
    {
        var source = CreateContext();
        source.Directives.EnqueuePostCommit(new TestContinuationJob(PostCommitContinuationBehavior.ContinueParent));
        var error = Error.Failure("PostCommit:Failed", "job failed", detail: "stack");
        var faultRequest = new PostCommitFaultRequest(error.Code, error.Message, error.Detail);
        _executor.ExecuteAsync(
                Arg.Any<IReadOnlyList<IPostCommitJob>>(),
                source,
                Arg.Any<CancellationToken>())
            .Returns(PostCommitResult.Fail(error, faultRequest));
        var coordinator = CreateCoordinator(new RecordingInlineStrategy());

        var result = await coordinator.CoordinateAsync(source, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.SourceContext.ShouldBeSameAs(source);
        result.Value.NextContext.ShouldBeNull();
        result.Value.FaultRequest.ShouldBeSameAs(faultRequest);
        result.Value.Error.ShouldBe(error);
    }

    [Fact]
    public async Task CoordinateAsync_WhenJobsMixContinuationOwnership_ShouldRejectConfiguration()
    {
        var source = CreateContext();
        source.Directives.EnqueuePostCommit(new TestContinuationJob(PostCommitContinuationBehavior.HandoffToChild));
        source.Directives.EnqueuePostCommit(new TestContinuationJob(PostCommitContinuationBehavior.ContinueParent));
        var coordinator = CreateCoordinator(new RecordingInlineStrategy());

        var result = await coordinator.CoordinateAsync(source, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ConfigInvalid);
        source.Directives.PostCommitJobs.ShouldBeEmpty();
        await _executor.DidNotReceiveWithAnyArgs()
            .ExecuteAsync(default!, default!, default);
    }

    [Fact]
    public async Task CoordinateAsync_WhenJobHasUndefinedContinuationOwnership_ShouldRejectBeforeExecution()
    {
        var source = CreateContext();
        source.Directives.EnqueuePostCommit(
            new TestContinuationJob((PostCommitContinuationBehavior)99));
        source.Directives.RequestNextTransition(new NextTransitionRequest("must-not-dispatch", "automatic"));
        _executor.ExecuteAsync(
                Arg.Any<IReadOnlyList<IPostCommitJob>>(),
                source,
                Arg.Any<CancellationToken>())
            .Returns(PostCommitResult.Ok());
        var coordinator = CreateCoordinator(new RecordingInlineStrategy());

        var result = await coordinator.CoordinateAsync(source, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ConfigInvalid);
        await _executor.DidNotReceiveWithAnyArgs()
            .ExecuteAsync(default!, default!, default);
    }

    private PostCommitTransitionCoordinator CreateCoordinator(IContinuationStrategy inlineStrategy) =>
        new(_executor, new ContinuationDispatcher([inlineStrategy]));

    private static TransitionExecutionContext CreateContext()
    {
        const string workflowKey = "test-workflow";
        const string domain = "test-domain";
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, workflowKey, "1.0.0", "test-key");

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Instance = instance,
            Workflow = CreateWorkflow(workflowKey, domain),
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = "test-transition",
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            Headers = new Dictionary<string, string?>()
        };
    }

    private static Definitions.Workflow CreateWorkflow(string key, string domain)
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(key, domain, "sys-flows", "1.0.0"));
        return workflow;
    }

    private sealed record TestContinuationJob(PostCommitContinuationBehavior ContinuationBehavior)
        : IPostCommitContinuationJob;

    private sealed class RecordingInlineStrategy(Action? beforeDispatch = null) : IContinuationStrategy
    {
        private readonly InlineContinuationStrategy _inner = new();

        public ContinuationMode Mode => ContinuationMode.Inline;
        public int DispatchCount { get; private set; }

        public Task<Result<WorkflowExecutionContext?>> DispatchAsync(
            TransitionExecutionContext current,
            CancellationToken cancellationToken)
        {
            beforeDispatch?.Invoke();
            DispatchCount++;
            return _inner.DispatchAsync(current, cancellationToken);
        }
    }
}
