using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.PostCommit;

/// <summary>
/// Pins the activation-episode rule on the post-commit settlement path: the
/// <c>Instance.Activation</c> span is emitted only AFTER the fresh unit of work commits (a client
/// observes the flip only then), never when the fresh parent is no longer Busy (a synchronous child
/// callback already settled — and closed the episode — in its own scope), and a post-commit fault
/// closes the episode as <c>faulted</c>.
/// </summary>
[Collection(TracingDetailLevelCollection.Name)]
public sealed class PostCommitParentMutationServiceActivationTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string WorkflowKey = "test-workflow";
    private const string WorkflowVersion = "1.0.0";
    // Literal, never PipelineStepActivityHelper.ActivitySource.Name (type-initializer re-entry).
    private const string PipelineSource = "BBT.Workflow.Pipeline";

    private readonly List<ActivityListener> _listeners = new();

    public void Dispose()
    {
        foreach (var l in _listeners) l.Dispose();
        Activity.Current = null;
    }

    /// <summary>
    /// A root span per test: an ActivityListener is process-wide, so spans of the same names emitted
    /// by test classes running in parallel would otherwise land in this test's collection. Everything
    /// the service emits here parents (directly or via the lane) under this root, so its trace id is
    /// the filter.
    /// </summary>
    private static Activity StartRoot()
    {
        var root = new Activity("test-root");
        root.SetIdFormat(ActivityIdFormat.W3C);
        root.Start();
        return root;
    }

    private List<Activity> Listen(Activity root, List<string>? calls = null)
    {
        var collected = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineSource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (a.TraceId != root.TraceId) return;
                lock (collected) collected.Add(a);
                if (a.DisplayName.StartsWith(ActivationActivity.SpanNamePrefix, StringComparison.Ordinal))
                    calls?.Add("activation");
            }
        };
        ActivitySource.AddActivityListener(listener);
        _listeners.Add(listener);
        return collected;
    }

    private static bool IsActivation(Activity a) =>
        a.DisplayName.StartsWith(ActivationActivity.SpanNamePrefix, StringComparison.Ordinal);

    [Fact]
    public async Task SettleAsync_FreshBusyParentResolvesToActive_EmitsActivationAfterCommit()
    {
        using var root = StartRoot();
        var calls = new List<string>();
        var collected = Listen(root, calls);
        var authoritative = CreateBusyInstance();
        var fixture = CreateFixture(authoritative, calls);

        var result = await fixture.Service.SettleAsync(
            CreateSnapshot(authoritative.Id),
            CreateContinuations(resolvedStatus: InstanceStatus.Active),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        calls.ShouldBe(["lock", "uow", "reload", "settle", "commit", "activation", "unlock"]);

        var activation = collected.Single(IsActivation);
        activation.DisplayName.ShouldBe("Instance.Activation/source-transition");
        activation.GetTagItem(TelemetryConstants.TagNames.ActivationOutcome).ShouldBe(TelemetryConstants.ActivationOutcomes.Active);
        activation.GetTagItem(TelemetryConstants.TagNames.SettleCas).ShouldBe("flipped");
        activation.GetTagItem(TelemetryConstants.TagNames.InstanceId).ShouldBe(authoritative.Id.ToString());

        var settle = collected.Single(a => a.DisplayName == "Transition.Settle");
        var commit = collected.Single(a => a.DisplayName == "Uow.Commit");
        activation.Links.Select(link => link.Context.SpanId).ShouldContain(commit.SpanId);
        activation.Links.Select(link => link.Context.SpanId).ShouldNotContain(root.SpanId);
        settle.GetTagItem(TelemetryConstants.TagNames.SettleCas).ShouldBe("flipped");
        settle.GetTagItem(TelemetryConstants.TagNames.ActivationEmitted).ShouldBe(true);
        settle.Events.Select(e => e.Name).ShouldContain("instance.available");

        // The emit must hand the caller back its ambient span.
        Activity.Current.ShouldBeSameAs(root);
    }

    [Fact]
    public async Task SettleAsync_FreshParentNoLongerBusy_EmitsNothing()
    {
        // A synchronous child callback already settled the parent in its own scope — and closed the
        // episode there. Emitting again here would double-count the same activation.
        using var root = StartRoot();
        var collected = Listen(root);
        var authoritative = Instance.Create(Guid.NewGuid(), WorkflowKey, WorkflowVersion, "instance-key");
        authoritative.Status.ShouldBe(InstanceStatus.Active);
        var fixture = CreateFixture(authoritative);

        var result = await fixture.Service.SettleAsync(
            CreateSnapshot(authoritative.Id),
            CreateContinuations(resolvedStatus: InstanceStatus.Active),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        collected.ShouldNotContain(a => IsActivation(a));
        collected.Single(a => a.DisplayName == "Transition.Settle")
            .GetTagItem(TelemetryConstants.TagNames.ActivationEmitted).ShouldBe(false);
    }

    [Fact]
    public async Task SettleAsync_ContinuationEnqueued_KeepsTheEpisodeOpen()
    {
        // The chain goes on in another job; this hop's settlement is not the rest point.
        using var root = StartRoot();
        var collected = Listen(root);
        var authoritative = CreateBusyInstance();
        var fixture = CreateFixture(authoritative);

        var result = await fixture.Service.SettleAsync(
            CreateSnapshot(authoritative.Id),
            CreateContinuations(resolvedStatus: null, continuationEnqueued: true),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        collected.ShouldNotContain(a => IsActivation(a));
    }

    [Fact]
    public async Task SettleAsync_HandoffToSubflow_KeepsTheEpisodeOpen()
    {
        // The parent remains Busy while the child runs; this is not the requested Available point.
        using var root = StartRoot();
        var collected = Listen(root);
        var authoritative = CreateBusyInstance();
        authoritative.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(), authoritative.Id, "state", Guid.NewGuid(), SubFlowType.SubFlow.Code, Domain, "child-flow", WorkflowVersion));
        var fixture = CreateFixture(authoritative);

        var result = await fixture.Service.SettleAsync(
            CreateSnapshot(authoritative.Id),
            CreateContinuations(resolvedStatus: null),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        collected.ShouldNotContain(a => IsActivation(a));
        collected.Single(a => a.DisplayName == "Transition.Settle")
            .GetTagItem(TelemetryConstants.TagNames.ActivationEmitted).ShouldBe(false);
    }

    [Fact]
    public async Task FaultAsync_FreshBusyParent_EmitsFaultedAfterCommit()
    {
        using var root = StartRoot();
        var calls = new List<string>();
        var collected = Listen(root, calls);
        var authoritative = CreateBusyInstance();
        var fixture = CreateFixture(authoritative, calls);

        var result = await fixture.Service.FaultAsync(
            CreateSnapshot(authoritative.Id),
            new PostCommitFaultRequest("boom", "Post:Boom", null),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        authoritative.Status.ShouldBe(InstanceStatus.Faulted);
        calls.ShouldBe(["lock", "uow", "reload", "update", "commit", "activation", "unlock"]);
        var activation = collected.Single(IsActivation);
        activation.GetTagItem(TelemetryConstants.TagNames.ActivationOutcome)
            .ShouldBe(TelemetryConstants.ActivationOutcomes.Faulted);
        var commit = collected.Single(a => a.DisplayName == "Uow.Commit");
        activation.Links.Select(link => link.Context.SpanId).ShouldContain(commit.SpanId);
    }

    [Fact]
    public async Task FaultAsync_ParentAlreadyTerminal_EmitsNothing()
    {
        using var root = StartRoot();
        var collected = Listen(root);
        var authoritative = CreateBusyInstance();
        authoritative.Complete(Domain);
        var fixture = CreateFixture(authoritative);

        var result = await fixture.Service.FaultAsync(
            CreateSnapshot(authoritative.Id),
            new PostCommitFaultRequest("boom", "Post:Boom", null),
            CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        collected.ShouldNotContain(a => IsActivation(a));
    }

    private static Fixture CreateFixture(Instance authoritative, List<string>? calls = null)
    {
        calls ??= [];

        var lockScope = Substitute.For<ITransitionLockScope>();
        lockScope.IsAcquired.Returns(true);
        lockScope.LockKey.Returns($"vnext:{Domain}:{WorkflowKey}:{authoritative.Id}");
        lockScope.DisposeAsync().Returns(_ =>
        {
            calls.Add("unlock");
            return ValueTask.CompletedTask;
        });

        var statusLock = Substitute.For<IInstanceStatusLock>();
        statusLock.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("lock");
                return Task.FromResult(lockScope);
            });

        var uow = Substitute.For<IUnitOfWork>();
        uow.CommitAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("commit");
            return Task.CompletedTask;
        });
        var uowManager = Substitute.For<IUnitOfWorkManager>();
        uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(_ =>
        {
            calls.Add("uow");
            return uow;
        });

        var repository = Substitute.For<IInstanceRepository>();
        repository.FindForPostCommitSettlementAsync(authoritative.Id, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("reload");
                return Task.FromResult<Instance?>(authoritative);
            });
        repository.UpdateAsync(authoritative, true, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("update");
                return Task.FromResult(authoritative);
            });
        repository.TryReleaseBusyAsync(authoritative, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("settle");
                authoritative.Active();
                return Task.FromResult(true);
            });

        var service = new PostCommitParentMutationService(
            uowManager,
            repository,
            statusLock,
            Substitute.For<IStateNotificationScheduler>(),
            NullLogger<PostCommitParentMutationService>.Instance);

        return new Fixture(service, repository);
    }

    private static Instance CreateBusyInstance()
    {
        var instance = Instance.Create(Guid.NewGuid(), WorkflowKey, WorkflowVersion, "instance-key");
        instance.Busy();
        return instance;
    }

    private static PostCommitParentSnapshot CreateSnapshot(Guid instanceId) => new(
        Domain,
        WorkflowKey,
        WorkflowVersion,
        instanceId,
        "source-transition",
        ExecMode.Sync,
        "trace-id",
        new Dictionary<string, string?>(),
        new Dictionary<string, string?>(),
        null,
        CreateWorkflow(State.Create("state", StateType.Intermediate, StateSubType.None, VersionStrategy.IncreaseMinor.Code)));

    private static ContinuationSet CreateContinuations(
        InstanceStatus? resolvedStatus = null,
        bool continuationEnqueued = false) => new(
        null,
        Array.Empty<IPostCommitJob>(),
        resolvedStatus,
        null,
        false,
        EpilogueMode.Run,
        continuationEnqueued);

    private static Definitions.Workflow CreateWorkflow(State state)
    {
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(WorkflowKey, Domain, "sys-flows", WorkflowVersion));
        workflow.AddState(state);
        return workflow;
    }

    private sealed record Fixture(PostCommitParentMutationService Service, IInstanceRepository Repository);
}
