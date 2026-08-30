using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Continuations;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Application.Tests.Execution.Services;

public sealed class TransitionRunnerPostCommitTests
{
    [Fact]
    public async Task RunAsync_ContinueParent_OrdersCommitAndPostCommitBeforeFreshStage()
    {
        var harness = new RunnerHarness(
            new StagePlan(
                "pipeline",
                HasDeferredEvent: true,
                PostCommitBehavior: PostCommitContinuationBehavior.ContinueParent,
                NextTransition: "fresh-parent-next"),
            new StagePlan("fresh-next-stage"));

        var result = await harness.Runner.RunAsync(harness.CreateInput("first"));

        result.IsSuccess.ShouldBeTrue();
        harness.BusinessCalls.ShouldBe([
            "pipeline",
            "stage-events",
            "commit",
            "post-commit",
            "fresh-next-stage",
            "commit"
        ]);
        harness.StageScopes.Count.ShouldBe(2);
        harness.StageScopes.Select(scope => scope.Id).Distinct().Count().ShouldBe(2);
        harness.CoreInstances.Distinct().Count().ShouldBe(2);
        harness.UowManagers.Distinct().Count().ShouldBe(2);
        harness.TransitionLockFactories.Distinct().Count().ShouldBe(2);
        harness.TransitionLocksAcquired.ShouldBe(2);
        harness.StageScopes.ShouldAllBe(scope => scope.IsDisposed && scope.UowDisposed);
        foreach (var uowManager in harness.UowManagers)
        {
            uowManager.Received(1).Begin(
                Arg.Is<UnitOfWorkOptions>(options => options.Scope == UnitOfWorkScopeOption.RequiresNew));
        }
        harness.PostCommitObservedDisposedStage.ShouldBeTrue();
        harness.PostCommitObservedReleasedTransitionLock.ShouldBeTrue();
        harness.CurrentUserScopes.Count.ShouldBe(3); // first stage, post-commit, fresh continuation
        harness.CurrentUserScopes.Select(scope => scope.UserId).ShouldBe(["42", "42", "42"]);
    }

    [Fact]
    public async Task RunAsync_WhenCommitFails_NeverRunsPostCommit()
    {
        var harness = new RunnerHarness(new StagePlan(
            "pipeline",
            HasDeferredEvent: true,
            PostCommitBehavior: PostCommitContinuationBehavior.ContinueParent,
            NextTransition: "must-not-run",
            FailCommit: true));

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => harness.Runner.RunAsync(harness.CreateInput("first")));

        exception.Message.ShouldBe("commit failed");
        harness.BusinessCalls.ShouldBe(["pipeline", "stage-events", "commit"]);
        harness.PostCommitExecutions.ShouldBe(0);
        harness.StageScopes.Single().IsDisposed.ShouldBeTrue();
        harness.StageScopes.Single().UowDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_HandoffToChild_AwaitsJobAndReturnsFreshAuthoritativeSettlementWithoutStaleContinuation()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authoritative = new TransitionOutput
        {
            Id = Guid.NewGuid(),
            Status = InstanceStatus.Completed
        };
        var harness = new RunnerHarness(new StagePlan(
            "pipeline",
            PostCommitBehavior: PostCommitContinuationBehavior.HandoffToChild,
            NextTransition: "stale-parent-next"))
        {
            PostCommitGate = gate
        };
        harness.ParentMutationService.SettleAsync(
                Arg.Any<PostCommitParentSnapshot>(),
                Arg.Any<ContinuationSet>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(authoritative));

        var runTask = harness.Runner.RunAsync(harness.CreateInput("first"));
        await harness.PostCommitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        runTask.IsCompleted.ShouldBeFalse();
        harness.CoreInstances.Count.ShouldBe(1);

        gate.SetResult();
        var result = await runTask;

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeSameAs(authoritative);
        harness.CoreInstances.Count.ShouldBe(1);
        harness.CoreTransitionKeys.ShouldNotContain("stale-parent-next");
        await harness.ParentMutationService.Received(1).SettleAsync(
            Arg.Is<PostCommitParentSnapshot>(snapshot => snapshot.InstanceId == harness.InstanceId),
            Arg.Is<ContinuationSet>(continuations => continuations.Next!.TransitionKey == "stale-parent-next"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_PostCommitFaultRequest_UsesFreshFaultMutationAndReturnsItsAuthoritativeOutput()
    {
        var authoritative = new TransitionOutput
        {
            Id = Guid.NewGuid(),
            Status = InstanceStatus.Faulted
        };
        var harness = new RunnerHarness(new StagePlan(
            "pipeline",
            PostCommitBehavior: PostCommitContinuationBehavior.HandoffToChild))
        {
            PostCommitResult = PostCommitResult.Fail(
                Error.Failure("PostCommit:Failed", "remote child failed", detail: "stack"),
                new PostCommitFaultRequest("PostCommit:Failed", "remote child failed", "stack"))
        };
        harness.ParentMutationService.FaultAsync(
                Arg.Any<PostCommitParentSnapshot>(),
                Arg.Any<PostCommitFaultRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(authoritative));

        var result = await harness.Runner.RunAsync(harness.CreateInput("first"));

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeSameAs(authoritative);
        await harness.ParentMutationService.Received(1).FaultAsync(
            Arg.Any<PostCommitParentSnapshot>(),
            Arg.Is<PostCommitFaultRequest>(request => request.ErrorCode == "PostCommit:Failed"),
            Arg.Any<CancellationToken>());
        await harness.ParentMutationService.DidNotReceiveWithAnyArgs()
            .SettleAsync(default!, default!, default);
    }

    [Fact]
    public async Task RunAsync_PostCommitErrorWithoutFaultRequest_ReturnsOriginalErrorWithoutParentMutation()
    {
        var error = Error.Validation("PostCommit:Rejected", "child rejected the request");
        var harness = new RunnerHarness(new StagePlan(
            "pipeline",
            PostCommitBehavior: PostCommitContinuationBehavior.HandoffToChild))
        {
            PostCommitResult = PostCommitResult.Fail(error)
        };

        var result = await harness.Runner.RunAsync(harness.CreateInput("first"));

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(error);
        await harness.ParentMutationService.DidNotReceiveWithAnyArgs()
            .SettleAsync(default!, default!, default);
        await harness.ParentMutationService.DidNotReceiveWithAnyArgs()
            .FaultAsync(default!, default!, default);
    }

    [Fact]
    public async Task RunAsync_AllowsDepthFiftyStageAndRejectsOnlyDepthFiftyOneContinuation()
    {
        var plans = Enumerable.Range(0, 51)
            .Select(depth => new StagePlan(
                $"pipeline-depth-{depth}",
                PostCommitBehavior: PostCommitContinuationBehavior.ContinueParent,
                NextTransition: $"next-depth-{depth + 1}"))
            .ToArray();
        var harness = new RunnerHarness(plans);

        var result = await harness.Runner.RunAsync(harness.CreateInput("first"));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.TransitionChainDepthExceeded);
        harness.CoreInstances.Count.ShouldBe(51);
        harness.CoreChainDepths.ShouldBe(Enumerable.Range(0, 51));
        harness.CoreTransitionKeys.ShouldNotContain("next-depth-51");
        harness.StageScopes.ShouldAllBe(scope => scope.IsDisposed && scope.UowDisposed);
    }

    private sealed class RunnerHarness
    {
        private const string Domain = "test-domain";
        private const string WorkflowKey = "test-workflow";
        private const string WorkflowVersion = "1.0.0";
        private readonly IReadOnlyList<StagePlan> _plans;
        private readonly WorkflowDefinition _workflow;
        private int _nextStage;
        private int _activeTransitionLocks;

        public RunnerHarness(params StagePlan[] plans)
        {
            _plans = plans;
            _workflow = CreateWorkflow();
            ParentMutationService = Substitute.For<IPostCommitParentMutationService>();

            var services = new ServiceCollection();
            ConfigureWorkflowScope(services);
            ConfigureStageServices(services);
            ConfigurePostCommitServices(services);

            var provider = services.BuildServiceProvider();
            Runner = new TransitionRunner(
                provider.GetRequiredService<IServiceScopeFactory>(),
                Substitute.For<ILogger<TransitionRunner>>());
        }

        public Guid InstanceId { get; } = Guid.NewGuid();
        public TransitionRunner Runner { get; }
        public IPostCommitParentMutationService ParentMutationService { get; }
        public List<string> BusinessCalls { get; } = [];
        public List<StageScopeProbe> StageScopes { get; } = [];
        public List<IWorkflowExecutionCore> CoreInstances { get; } = [];
        public List<IUnitOfWorkManager> UowManagers { get; } = [];
        public List<ITransitionLockScopeFactory> TransitionLockFactories { get; } = [];
        public List<CurrentUserScopeProbe> CurrentUserScopes { get; } = [];
        public List<string> CoreTransitionKeys { get; } = [];
        public List<int> CoreChainDepths { get; } = [];
        public int TransitionLocksAcquired { get; private set; }
        public int PostCommitExecutions { get; private set; }
        public bool PostCommitObservedDisposedStage { get; private set; }
        public bool PostCommitObservedReleasedTransitionLock { get; private set; }
        public TaskCompletionSource PostCommitStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? PostCommitGate { get; init; }
        public PostCommitResult PostCommitResult { get; init; } = PostCommitResult.Ok();

        public WorkflowExecutionContext CreateInput(string transitionKey) => new()
        {
            Domain = Domain,
            WorkflowKey = WorkflowKey,
            WorkflowVersion = WorkflowVersion,
            InstanceId = InstanceId.ToString(),
            TransitionKey = transitionKey,
            Mode = ExecMode.Sync,
            CallerMode = ExecMode.Sync,
            Headers = new Dictionary<string, string?> { ["userId"] = "42" }
        };

        private void ConfigureWorkflowScope(IServiceCollection services)
        {
            var currentSchema = Substitute.For<ICurrentSchema>();
            currentSchema.Change(Arg.Any<string>()).Returns(Substitute.For<IDisposable>());
            var cacheStore = Substitute.For<IComponentCacheStore>();
            cacheStore.GetFlowAsync(
                    Domain,
                    WorkflowKey,
                    Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .Returns(Result<WorkflowDefinition>.Ok(_workflow));

            services.AddSingleton(currentSchema);
            services.AddSingleton(cacheStore);
            services.AddSingleton(Substitute.For<ISubflowTerminalRelay>());
            services.AddScoped(_ =>
            {
                var probe = new CurrentUserScopeProbe();
                var currentUser = Substitute.For<ICurrentUser>();
                currentUser.Change(
                        Arg.Any<string?>(),
                        Arg.Any<string?>(),
                        Arg.Any<string?>(),
                        Arg.Any<string?>(),
                        Arg.Any<string[]?>(),
                        Arg.Any<string?>(),
                        Arg.Any<string?>(),
                        Arg.Any<string?>())
                    .Returns(call =>
                    {
                        probe.UserId = call.ArgAt<string?>(0);
                        return Substitute.For<IDisposable>();
                    });
                CurrentUserScopes.Add(probe);
                return currentUser;
            });
        }

        private void ConfigureStageServices(IServiceCollection services)
        {
            services.AddScoped(_ =>
            {
                var stage = Interlocked.Increment(ref _nextStage);
                var probe = new StageScopeProbe(stage);
                StageScopes.Add(probe);
                return probe;
            });
            services.AddScoped<ITransitionLockScopeFactory>(sp =>
            {
                var factory = new RecordingTransitionLockScopeFactory(this);
                TransitionLockFactories.Add(factory);
                return factory;
            });
            services.AddScoped<IWorkflowExecutionCore>(sp =>
            {
                var probe = sp.GetRequiredService<StageScopeProbe>();
                var core = new RecordingCore(
                    this,
                    sp.GetRequiredService<ITransitionLockScopeFactory>(),
                    _plans[probe.Id - 1],
                    _workflow);
                CoreInstances.Add(core);
                return core;
            });
            services.AddScoped<IUnitOfWorkManager>(sp =>
            {
                var probe = sp.GetRequiredService<StageScopeProbe>();
                var uow = Substitute.For<IUnitOfWork>();
                uow.CommitAsync(Arg.Any<CancellationToken>()).Returns(_ =>
                {
                    BusinessCalls.Add("commit");
                    if (_plans[probe.Id - 1].FailCommit)
                        throw new InvalidOperationException("commit failed");
                    return Task.CompletedTask;
                });
                uow.DisposeAsync().Returns(_ =>
                {
                    probe.UowDisposed = true;
                    return ValueTask.CompletedTask;
                });

                var manager = Substitute.For<IUnitOfWorkManager>();
                manager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(uow);
                UowManagers.Add(manager);
                return manager;
            });
            services.AddSingleton(CreateEventBus());
        }

        private void ConfigurePostCommitServices(IServiceCollection services)
        {
            services.AddScoped<IContinuationStrategy, InlineContinuationStrategy>();
            services.AddScoped<ContinuationDispatcher>();
            services.AddScoped<IPostCommitTransitionCoordinator, PostCommitTransitionCoordinator>();
            services.AddSingleton<IPostCommitExecutor>(new RecordingPostCommitExecutor(this));
            services.AddSingleton(ParentMutationService);
        }

        private IDistributedEventBus CreateEventBus()
        {
            var eventBus = Substitute.For<IDistributedEventBus>();
            eventBus.PublishAsync(
                    Arg.Any<IDistributedEvent>(),
                    Arg.Any<EventMetadata>(),
                    Arg.Any<string?>(),
                    Arg.Any<bool>(),
                    Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    BusinessCalls.Add("stage-events");
                    return Task.CompletedTask;
                });
            return eventBus;
        }

        private async Task<PostCommitResult> ExecutePostCommitAsync(
            TransitionExecutionContext source,
            CancellationToken cancellationToken)
        {
            PostCommitExecutions++;
            PostCommitObservedDisposedStage = StageScopes.Last().IsDisposed && StageScopes.Last().UowDisposed;
            PostCommitObservedReleasedTransitionLock =
                Volatile.Read(ref _activeTransitionLocks) == 0;
            PostCommitStarted.TrySetResult();
            if (PostCommitGate is not null)
                await PostCommitGate.Task.WaitAsync(cancellationToken);
            BusinessCalls.Add("post-commit");
            return PostCommitResult;
        }

        private sealed class RecordingPostCommitExecutor(RunnerHarness owner) : IPostCommitExecutor
        {
            public Task<PostCommitResult> ExecuteAsync(
                IReadOnlyList<IPostCommitJob> jobs,
                TransitionExecutionContext context,
                CancellationToken cancellationToken) => owner.ExecutePostCommitAsync(context, cancellationToken);
        }

        private sealed class RecordingCore(
            RunnerHarness owner,
            ITransitionLockScopeFactory lockScopeFactory,
            StagePlan plan,
            WorkflowDefinition workflow) : IWorkflowExecutionCore
        {
            public async Task<Result<TransitionCoreOutput>> ExecuteTransitionCoreAsync(
                WorkflowExecutionContext context,
                CancellationToken cancellationToken = default)
            {
                owner.CoreTransitionKeys.Add(context.TransitionKey);
                owner.CoreChainDepths.Add(context.Execution?.ChainDepth ?? 0);
                context.Headers["x-transition"] = context.TransitionKey;
                owner.BusinessCalls.Add(plan.Label);

                var instance = Instance.Create(owner.InstanceId, WorkflowKey, WorkflowVersion, "instance-key");
                instance.Busy();
                var executionContext = new TransitionExecutionContext
                {
                    Domain = Domain,
                    WorkflowKey = WorkflowKey,
                    InstanceId = owner.InstanceId,
                    TransitionKey = context.TransitionKey,
                    Workflow = workflow,
                    Instance = instance,
                    CallerMode = context.CallerMode,
                    Mode = context.Mode,
                    Headers = context.Headers,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                    ExecutionChainId = Guid.NewGuid().ToString("N"),
                    ChainDepth = context.Execution?.ChainDepth ?? 0,
                    TraceId = Guid.NewGuid().ToString("N")
                };

                await using (var transitionLock = await lockScopeFactory.AcquireAsync(
                                 executionContext.LockKey,
                                 cancellationToken))
                {
                    transitionLock.IsAcquired.ShouldBeTrue();
                }

                if (plan.PostCommitBehavior is { } behavior)
                    executionContext.Directives.EnqueuePostCommit(new TestJob(behavior));
                if (plan.NextTransition is { } next)
                    executionContext.Directives.RequestNextTransition(new NextTransitionRequest(next, "automatic"));

                IReadOnlyList<DomainEventEnvelope> events = plan.HasDeferredEvent
                    ? [new DomainEventEnvelope(
                        new TestEvent(),
                        new EventMetadata(typeof(TestEvent), "test.event", 1, "pubsub", "topic", "source"))]
                    : Array.Empty<DomainEventEnvelope>();
                var output = new TransitionOutput
                {
                    Id = owner.InstanceId,
                    Status = InstanceStatus.Busy,
                    PipelineInstance = instance
                };

                return Result<TransitionCoreOutput>.Ok(new TransitionCoreOutput(
                    output,
                    events,
                    executionContext.Directives.ToContinuations(),
                    executionContext));
            }
        }

        private sealed class RecordingTransitionLockScopeFactory(RunnerHarness owner)
            : ITransitionLockScopeFactory
        {
            public Task<ITransitionLockScope> AcquireAsync(
                string lockKey,
                CancellationToken cancellationToken = default)
            {
                owner.TransitionLocksAcquired++;
                Interlocked.Increment(ref owner._activeTransitionLocks);
                return Task.FromResult<ITransitionLockScope>(new RecordingTransitionLockScope(owner, lockKey));
            }

            public Task<ITransitionLockScope> AcquireAsync(
                string lockKey,
                LockAcquireWait wait,
                CancellationToken cancellationToken = default)
                => AcquireAsync(lockKey, cancellationToken);
        }

        private sealed class RecordingTransitionLockScope(RunnerHarness owner, string lockKey)
            : ITransitionLockScope
        {
            private int _disposed;
            public bool IsAcquired => true;
            public string LockKey => lockKey;
            public Task<bool> ExtendAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Decrement(ref owner._activeTransitionLocks);
                return ValueTask.CompletedTask;
            }
        }

        private static WorkflowDefinition CreateWorkflow()
        {
            var workflow = WorkflowDefinition.Create();
            workflow.SetReference(new Reference(WorkflowKey, Domain, "sys-flows", WorkflowVersion));
            return workflow;
        }
    }

    private sealed class StageScopeProbe(int id) : IDisposable
    {
        public int Id { get; } = id;
        public bool UowDisposed { get; set; }
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class CurrentUserScopeProbe
    {
        public string? UserId { get; set; }
    }

    private sealed record StagePlan(
        string Label,
        bool HasDeferredEvent = false,
        PostCommitContinuationBehavior? PostCommitBehavior = null,
        string? NextTransition = null,
        bool FailCommit = false);

    private sealed record TestJob(PostCommitContinuationBehavior ContinuationBehavior)
        : IPostCommitContinuationJob;

    private sealed class TestEvent : IDistributedEvent;
}
