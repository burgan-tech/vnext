using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline;

/// <summary>
/// Pins what <see cref="TransitionSettlement"/> now says about the activation episode: the
/// <c>vnext.settle.cas</c> tag tells a flip from a lost race from a skipped guard (the one thing
/// <c>vnext.settle.status</c> could not), the <c>instance.available</c> event fires exactly on a flip,
/// and the <see cref="ActivationVerdict"/> recorded on the directives names the rest point — or stays
/// null when the episode is not this hop's to close.
/// </summary>
[Collection(TracingDetailLevelCollection.Name)]
public sealed class TransitionSettlementVerdictTests : IDisposable
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
    /// Installs a listener scoped to a fresh root span's trace: an ActivityListener is process-wide,
    /// so `Transition.Settle` spans emitted by test classes running in parallel would otherwise land
    /// here too. The root stays Activity.Current for the test body, so the settlement's span parents
    /// under it.
    /// </summary>
    private List<Activity> Listen()
    {
        var root = new Activity("test-root");
        root.SetIdFormat(ActivityIdFormat.W3C);
        root.Start();

        var collected = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineSource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (a.TraceId != root.TraceId) return;
                lock (collected) collected.Add(a);
            }
        };
        ActivitySource.AddActivityListener(listener);
        _listeners.Add(listener);
        return collected;
    }

    [Fact]
    public async Task Flip_RecordsActiveVerdict_TagsCasFlipped_AndRaisesInstanceAvailable()
    {
        var collected = Listen();
        var context = CreateContext(CreateBusyInstance(), ownsStatus: true);
        var repository = Substitute.For<IInstanceRepository>();
        repository.TryReleaseBusyAsync(context.Instance, Arg.Any<CancellationToken>()).Returns(true);

        await Apply(context, InstanceStatus.Active, repository, chainSettled: true);

        var verdict = context.Directives.Activation.ShouldNotBeNull();
        verdict.Outcome.ShouldBe(TelemetryConstants.ActivationOutcomes.Active);
        verdict.CasFlipped.ShouldBeTrue();
        verdict.StateTo.ShouldBe("waiting");

        var settle = collected.Single(a => a.DisplayName == "Transition.Settle");
        settle.GetTagItem(TelemetryConstants.TagNames.SettleCas).ShouldBe("flipped");
        settle.GetTagItem(TelemetryConstants.TagNames.ActivationEmitted).ShouldBe(true);
        var available = settle.Events.Single(e => e.Name == "instance.available");
        available.Tags.ShouldContain(t => t.Key == TelemetryConstants.TagNames.StateTo && (string?)t.Value == "waiting");
    }

    [Fact]
    public async Task LostCas_RecordsNoVerdict_AndTagsCasLost()
    {
        // The row was no longer Busy: whoever flipped it closed the episode and emits its span.
        var collected = Listen();
        var context = CreateContext(CreateBusyInstance(), ownsStatus: true);
        var repository = Substitute.For<IInstanceRepository>();
        repository.TryReleaseBusyAsync(context.Instance, Arg.Any<CancellationToken>()).Returns(false);

        await Apply(context, InstanceStatus.Active, repository, chainSettled: true);

        context.Directives.Activation.ShouldBeNull();
        var settle = collected.Single(a => a.DisplayName == "Transition.Settle");
        settle.GetTagItem(TelemetryConstants.TagNames.SettleCas).ShouldBe("lost");
        settle.GetTagItem(TelemetryConstants.TagNames.ActivationEmitted).ShouldBe(false);
        settle.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task NonOwner_RecordsNoVerdict_AndSkipsTheGuard()
    {
        // updateData beside an in-flight chain: it never made the instance unavailable.
        var collected = Listen();
        var context = CreateContext(CreateBusyInstance(), ownsStatus: false);
        var repository = Substitute.For<IInstanceRepository>();

        await Apply(context, InstanceStatus.Active, repository, chainSettled: true);

        context.Directives.Activation.ShouldBeNull();
        await repository.DidNotReceive().TryReleaseBusyAsync(Arg.Any<Instance>(), Arg.Any<CancellationToken>());
        collected.Single(a => a.DisplayName == "Transition.Settle")
            .GetTagItem(TelemetryConstants.TagNames.SettleCas).ShouldBe("skipped");
    }

    [Fact]
    public async Task ChainNotSettled_RecordsNoVerdict_EvenWhenTheFlipHappens()
    {
        // The next transition was enqueued as another job: that job closes the episode.
        Listen();
        var context = CreateContext(CreateBusyInstance(), ownsStatus: true);
        var repository = Substitute.For<IInstanceRepository>();
        repository.TryReleaseBusyAsync(context.Instance, Arg.Any<CancellationToken>()).Returns(true);

        await Apply(context, InstanceStatus.Active, repository, chainSettled: false);

        context.Directives.Activation.ShouldBeNull();
    }

    [Fact]
    public async Task BusySubtypeTarget_RestsAsBusySubtype()
    {
        Listen();
        var target = State.Create("hold", StateType.Intermediate, StateSubType.Busy, VersionStrategy.IncreaseMinor.Code);
        var context = CreateContext(CreateBusyInstance(), ownsStatus: true, target: target);

        await Apply(context, InstanceStatus.Active, Substitute.For<IInstanceRepository>(), chainSettled: true);

        var verdict = context.Directives.Activation.ShouldNotBeNull();
        verdict.Outcome.ShouldBe(TelemetryConstants.ActivationOutcomes.BusySubtype);
        verdict.CasFlipped.ShouldBeFalse();
        verdict.StateTo.ShouldBe("hold");
    }

    [Fact]
    public async Task OpenSubFlowCorrelation_RestsAsBusySubflow()
    {
        Listen();
        var instance = CreateBusyInstance();
        instance.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(), instance.Id, "waiting", Guid.NewGuid(), SubFlowType.SubFlow.Code, Domain, "child-flow", WorkflowVersion));
        var context = CreateContext(instance, ownsStatus: true);

        await Apply(context, resolvedStatus: null, Substitute.For<IInstanceRepository>(), chainSettled: true);

        context.Directives.Activation.ShouldNotBeNull().Outcome.ShouldBe(TelemetryConstants.ActivationOutcomes.BusySubflow);
    }

    [Fact]
    public async Task BusyWithNothingResolved_RestsAsBusyParked()
    {
        // A state whose automatic transitions did not fire: the instance parks Busy at rest.
        Listen();
        var context = CreateContext(CreateBusyInstance(), ownsStatus: true);

        await Apply(context, resolvedStatus: null, Substitute.For<IInstanceRepository>(), chainSettled: true);

        context.Directives.Activation.ShouldNotBeNull().Outcome.ShouldBe(TelemetryConstants.ActivationOutcomes.BusyParked);
    }

    [Fact]
    public async Task CompletedInstance_RestsAsCompleted()
    {
        Listen();
        var instance = CreateBusyInstance();
        instance.Complete(Domain);
        var context = CreateContext(instance, ownsStatus: true);

        await Apply(context, resolvedStatus: null, Substitute.For<IInstanceRepository>(), chainSettled: true);

        context.Directives.Activation.ShouldNotBeNull().Outcome.ShouldBe(TelemetryConstants.ActivationOutcomes.Completed);
    }

    [Fact]
    public async Task CanceledInstance_RestsAsCanceled()
    {
        // Instance.Cancel writes Completed; the cancel transition running tells the two apart.
        Listen();
        var instance = CreateBusyInstance();
        instance.Cancel(Domain);
        var cancel = Transition.Create(
            WellKnownTransitionKeys.Cancel, null, "canceled", TriggerType.Manual, VersionStrategy.IncreaseMinor.Code);
        var context = CreateContext(instance, ownsStatus: true, transition: cancel);

        await Apply(context, resolvedStatus: null, Substitute.For<IInstanceRepository>(), chainSettled: true);

        context.Directives.Activation.ShouldNotBeNull().Outcome.ShouldBe(TelemetryConstants.ActivationOutcomes.Canceled);
    }

    [Fact]
    public async Task FaultedInstance_RestsAsFaulted()
    {
        Listen();
        var instance = CreateBusyInstance();
        instance.Fault(Domain);
        var context = CreateContext(instance, ownsStatus: true);

        await Apply(context, resolvedStatus: null, Substitute.For<IInstanceRepository>(), chainSettled: true);

        context.Directives.Activation.ShouldNotBeNull().Outcome.ShouldBe(TelemetryConstants.ActivationOutcomes.Faulted);
    }

    [Fact]
    public async Task AlreadyActiveInstance_RecordsNoVerdict()
    {
        // Nothing became available here: the instance was resting Active before this hop.
        Listen();
        var instance = Instance.Create(Guid.NewGuid(), WorkflowKey, WorkflowVersion, "instance-key");
        instance.Status.ShouldBe(InstanceStatus.Active);
        var context = CreateContext(instance, ownsStatus: true);

        await Apply(context, resolvedStatus: null, Substitute.For<IInstanceRepository>(), chainSettled: true);

        context.Directives.Activation.ShouldBeNull();
    }

    private static Task Apply(
        TransitionExecutionContext context,
        InstanceStatus? resolvedStatus,
        IInstanceRepository repository,
        bool chainSettled)
        => TransitionSettlement.ApplyAsync(
            context,
            resolvedStatus,
            scheduleNotification: false,
            repository,
            Substitute.For<IStateNotificationScheduler>(),
            NullLogger.Instance,
            CancellationToken.None,
            chainSettled);

    private static Instance CreateBusyInstance()
    {
        var instance = Instance.Create(Guid.NewGuid(), WorkflowKey, WorkflowVersion, "instance-key");
        instance.Busy();
        return instance;
    }

    private static TransitionExecutionContext CreateContext(
        Instance instance,
        bool ownsStatus,
        State? target = null,
        Transition? transition = null)
    {
        var state = target ?? State.Create("waiting", StateType.Intermediate, StateSubType.None, VersionStrategy.IncreaseMinor.Code);
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(WorkflowKey, Domain, "sys-flows", WorkflowVersion));
        workflow.AddState(state);

        return new TransitionExecutionContext
        {
            Domain = Domain,
            WorkflowKey = WorkflowKey,
            InstanceId = instance.Id,
            TransitionKey = transition?.Key ?? "go",
            Transition = transition,
            Workflow = workflow,
            Instance = instance,
            Current = state,
            Target = state,
            OwnsStatus = ownsStatus
        };
    }
}
