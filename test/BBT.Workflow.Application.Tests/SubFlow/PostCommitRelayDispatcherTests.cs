using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.PostCommit.Relay;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

/// <summary>
/// Pins <see cref="PostCommitRelayDispatcher"/> and the relays registered on it: an event whose
/// runtime type has a registered <see cref="IPostCommitEventRelay{TEvent}"/> is delivered to its
/// receiver immediately through <see cref="IInstanceCommandGateway"/>; one without a registration is
/// skipped and travels the outbox alone. Every failure is swallowed and logged, because the
/// originating commit already stands and the event's outbox row guarantees the Inbox backup.
/// </summary>
public sealed class PostCommitRelayDispatcherTests : IDisposable
{
    private readonly List<Activity> _collected = new();
    private readonly ActivityListener _listener;

    public PostCommitRelayDispatcherTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "BBT.Workflow.Pipeline",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _collected.Add
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        Activity.Current = null;
    }

    private static DomainEventEnvelope Envelope(IDistributedEvent evt) =>
        new(evt, new EventMetadata(evt.GetType(), "test.event", 1, "pubsub", "topic", "source"));

    private static IRuntimeInfoProvider MatchingRuntime()
    {
        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.IsDomainMatch(Arg.Any<string?>()).Returns(true);
        return runtime;
    }

    /// <summary>
    /// The production registrations, mirroring <c>AddPipelineServices</c>. Registration IS the
    /// opt-in, so the set built here is exactly what decides which events relay.
    /// </summary>
    private static ServiceProvider Registrations(
        IInstanceCommandGateway gateway,
        Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(gateway);
        services.AddScoped<IPostCommitEventRelay<InstanceSubCompletedEvent>, InstanceSubCompletedRelay>();
        services.AddScoped<IPostCommitEventRelay<InstanceSubFaultedEvent>, InstanceSubFaultedRelay>();
        services.AddScoped<IPostCommitEventRelay<InstanceSubCanceledEvent>, InstanceSubCanceledRelay>();
        services.AddScoped<IPostCommitEventRelay<InstanceSubStateChangedEvent>, InstanceSubStateChangedRelay>();
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static PostCommitRelayDispatcher Dispatcher(IServiceProvider provider, IRuntimeInfoProvider runtime) =>
        new(provider, runtime, NullLogger<PostCommitRelayDispatcher>.Instance);

    private static InstanceSubCompletedEvent CompletedEvent(bool sync = false) => new()
    {
        InstanceId = Guid.NewGuid(),
        Domain = "orders",
        Flow = "order-flow",
        Version = "1.0.0",
        SubInstanceId = Guid.NewGuid(),
        CompletedState = "done",
        CompletedAt = DateTime.UtcNow,
        Sync = sync
    };

    private static InstanceSubStateChangedEvent StateChangedEvent() => new()
    {
        ParentInstanceId = Guid.NewGuid(),
        SubInstanceId = Guid.NewGuid(),
        Domain = "orders",
        Flow = "order-flow",
        Version = "1.0.0",
        NewState = "running",
        PreviousState = "start",
        NewStateType = 2,
        NewStateSubType = 0,
        ChangedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task Relays_SubCompleted_Through_Gateway_Complete()
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        gateway.CompleteAsync(Arg.Any<FlowCompletedInput>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());
        var sut = Dispatcher(Registrations(gateway), MatchingRuntime());

        var evt = CompletedEvent(sync: true);
        await sut.RelayAsync([Envelope(evt)], CancellationToken.None);

        await gateway.Received(1).CompleteAsync(
            Arg.Is<FlowCompletedInput>(i => i.Sync && i.SubInstanceId == evt.SubInstanceId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Relays_SubFaulted_And_SubCanceled_To_Their_Gateway_Methods()
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        gateway.FaultAsync(Arg.Any<SubFlowFaultedInput>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());
        gateway.CancelAsync(Arg.Any<SubItemCanceledInput>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());
        var sut = Dispatcher(Registrations(gateway), MatchingRuntime());

        var faulted = new InstanceSubFaultedEvent
        {
            InstanceId = Guid.NewGuid(),
            Domain = "orders",
            Flow = "order-flow",
            Version = "1.0.0",
            SubInstanceId = Guid.NewGuid(),
            FaultedState = "error",
            FaultedAt = DateTime.UtcNow
        };
        var canceled = new InstanceSubCanceledEvent
        {
            InstanceId = Guid.NewGuid(),
            Domain = "orders",
            Flow = "order-flow",
            Version = "1.0.0",
            SubInstanceId = Guid.NewGuid(),
            CanceledState = "canceled",
            CanceledAt = DateTime.UtcNow,
            SubItemType = SubItemType.SubFlow,
            TerminationOrigin = TerminationOrigin.Direct,
            InitiatorInstanceId = Guid.NewGuid(),
            CascadeId = Guid.NewGuid()
        };

        await sut.RelayAsync([Envelope(faulted), Envelope(canceled)], CancellationToken.None);

        await gateway.Received(1).FaultAsync(
            Arg.Is<SubFlowFaultedInput>(i => i.SubInstanceId == faulted.SubInstanceId),
            Arg.Any<CancellationToken>());
        await gateway.Received(1).CancelAsync(
            Arg.Is<SubItemCanceledInput>(i => i.SubInstanceId == canceled.SubInstanceId),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The inverse of what the terminal-only relay pinned before this change: the sub-state event is
    /// now a registered, relayed event rather than one the dispatcher steps over.
    /// </summary>
    [Fact]
    public async Task Relays_SubStateChanged_Through_Gateway_UpdateSubFlowState()
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        gateway.UpdateSubFlowStateAsync(Arg.Any<SubFlowStateChangedInput>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());
        var sut = Dispatcher(Registrations(gateway), MatchingRuntime());

        var evt = StateChangedEvent();
        await sut.RelayAsync([Envelope(evt)], CancellationToken.None);

        await gateway.Received(1).UpdateSubFlowStateAsync(
            Arg.Is<SubFlowStateChangedInput>(i =>
                i.SubInstanceId == evt.SubInstanceId
                && i.ParentInstanceId == evt.ParentInstanceId
                && i.NewState == evt.NewState
                && i.ChangedAt == evt.ChangedAt),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Skips_Events_With_No_Registered_Relay()
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        var sut = Dispatcher(Registrations(gateway), MatchingRuntime());

        await sut.RelayAsync([Envelope(new UnregisteredEvent())], CancellationToken.None);

        gateway.ReceivedCalls().ShouldBeEmpty();
    }

    /// <summary>
    /// The extensibility contract from the council decision: opting a new event into the relay path
    /// is one relay class plus one registration — no edit to the dispatcher, and no marker on the
    /// event contract.
    /// </summary>
    [Fact]
    public async Task Registering_A_Relay_For_A_New_Event_Type_Needs_No_Dispatcher_Change()
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        var relay = new RecordingRelay();
        var provider = Registrations(gateway, s => s.AddSingleton<IPostCommitEventRelay<UnregisteredEvent>>(relay));
        var sut = Dispatcher(provider, MatchingRuntime());

        await sut.RelayAsync([Envelope(new UnregisteredEvent())], CancellationToken.None);

        relay.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Gateway_Failure_Is_Swallowed_And_Logged()
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        gateway.CompleteAsync(Arg.Any<FlowCompletedInput>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result>>(_ => throw new InvalidOperationException("boom"));
        var sut = Dispatcher(Registrations(gateway), MatchingRuntime());

        await Should.NotThrowAsync(() => sut.RelayAsync([Envelope(CompletedEvent())], CancellationToken.None));

        _collected.Single(a => a.DisplayName == "PostCommit.EventRelay")
            .GetTagItem(TelemetryConstants.TagNames.RelayOutcome)
            .ShouldBe(PostCommitRelayOutcomes.Failed);
    }

    [Fact]
    public async Task Gateway_ResultFail_Is_Swallowed_And_Logged()
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        gateway.CompleteAsync(Arg.Any<FlowCompletedInput>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail(Error.Failure("test.failure", "nope")));
        var sut = Dispatcher(Registrations(gateway), MatchingRuntime());

        await Should.NotThrowAsync(() => sut.RelayAsync([Envelope(CompletedEvent())], CancellationToken.None));

        await gateway.Received(1).CompleteAsync(Arg.Any<FlowCompletedInput>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tags_RelayRoute_Local_And_Remote()
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        gateway.CompleteAsync(Arg.Any<FlowCompletedInput>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());

        var localRuntime = Substitute.For<IRuntimeInfoProvider>();
        localRuntime.IsDomainMatch("orders").Returns(true);
        await Dispatcher(Registrations(gateway), localRuntime)
            .RelayAsync([Envelope(CompletedEvent())], CancellationToken.None);

        var remoteRuntime = Substitute.For<IRuntimeInfoProvider>();
        remoteRuntime.IsDomainMatch("orders").Returns(false);
        await Dispatcher(Registrations(gateway), remoteRuntime)
            .RelayAsync([Envelope(CompletedEvent())], CancellationToken.None);

        var spans = _collected.Where(a => a.DisplayName == "PostCommit.EventRelay").ToList();
        spans.Count.ShouldBe(2);
        spans[0].GetTagItem(TelemetryConstants.TagNames.RelayRoute).ShouldBe("local");
        spans[1].GetTagItem(TelemetryConstants.TagNames.RelayRoute).ShouldBe("remote");
        spans.ShouldAllBe(s => Equals(s.GetTagItem(TelemetryConstants.TagNames.DeliveryRole), "relay"));
    }

    /// <summary>
    /// The cross-domain bound belongs to the relay, not the dispatcher: a terminal relay declares no
    /// timeout and keeps its historical unbounded behaviour, while the sub-state relay's own bound
    /// releases the hop and reports <c>timeout</c>.
    /// </summary>
    [Fact]
    public async Task Remote_Leg_That_Outruns_The_Relays_Bound_Reports_Timeout()
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        gateway.UpdateSubFlowStateAsync(Arg.Any<SubFlowStateChangedInput>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return Result.Ok();
            });

        var remoteRuntime = Substitute.For<IRuntimeInfoProvider>();
        remoteRuntime.IsDomainMatch("orders").Returns(false);

        var provider = Registrations(gateway, s =>
        {
            s.RemoveAll<IPostCommitEventRelay<InstanceSubStateChangedEvent>>();
            s.AddSingleton<IPostCommitEventRelay<InstanceSubStateChangedEvent>>(
                new ShortTimeoutStateRelay(gateway));
        });

        await Should.NotThrowAsync(() => Dispatcher(provider, remoteRuntime)
            .RelayAsync([Envelope(StateChangedEvent())], CancellationToken.None));

        _collected.Single(a => a.DisplayName == "PostCommit.EventRelay")
            .GetTagItem(TelemetryConstants.TagNames.RelayOutcome)
            .ShouldBe(PostCommitRelayOutcomes.Timeout);
    }

    /// <summary>An event nothing registers a relay for.</summary>
    private sealed class UnregisteredEvent : IDistributedEvent;

    private sealed class RecordingRelay : IPostCommitEventRelay<UnregisteredEvent>
    {
        public int Calls { get; private set; }

        public PostCommitRelayTarget Describe(UnregisteredEvent @event)
            => new("orders", Guid.NewGuid(), Guid.NewGuid());

        public Task<Result> RelayAsync(UnregisteredEvent @event, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result.Ok());
        }
    }

    /// <summary>Same shape as the production sub-state relay, with a bound short enough to test.</summary>
    private sealed class ShortTimeoutStateRelay(IInstanceCommandGateway gateway)
        : IPostCommitEventRelay<InstanceSubStateChangedEvent>
    {
        public PostCommitRelayTarget Describe(InstanceSubStateChangedEvent @event)
            => new(@event.Domain, @event.ParentInstanceId, @event.SubInstanceId,
                Sync: null, TimeSpan.FromMilliseconds(50));

        public Task<Result> RelayAsync(InstanceSubStateChangedEvent @event, CancellationToken cancellationToken)
            => gateway.UpdateSubFlowStateAsync(
                new SubFlowStateChangedInput
                {
                    ParentInstanceId = @event.ParentInstanceId,
                    SubInstanceId = @event.SubInstanceId,
                    Domain = @event.Domain,
                    Flow = @event.Flow,
                    Version = @event.Version,
                    NewState = @event.NewState,
                    PreviousState = @event.PreviousState,
                    NewStateType = (StateType)@event.NewStateType,
                    NewStateSubType = (StateSubType)@event.NewStateSubType,
                    ChangedAt = @event.ChangedAt
                },
                cancellationToken);
    }
}
