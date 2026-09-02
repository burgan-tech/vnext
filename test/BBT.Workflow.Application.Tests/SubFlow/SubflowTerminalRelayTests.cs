using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Results;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

/// <summary>
/// Pins <see cref="SubflowTerminalRelay"/>: it selects <c>ISubflowTerminalEvent</c> payloads from
/// deferred events and settles the parent immediately through <see cref="IInstanceCommandGateway"/>,
/// swallowing (and logging) any failure because the event's own outbox row guarantees the Inbox
/// backup will settle the parent shortly after.
/// </summary>
public sealed class SubflowTerminalRelayTests : IDisposable
{
    private readonly List<Activity> _collected = new();
    private readonly ActivityListener _listener;

    public SubflowTerminalRelayTests()
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

    [Fact]
    public async Task Relays_SubCompleted_Through_Gateway_Complete()
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        gateway.CompleteAsync(Arg.Any<FlowCompletedInput>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok());
        var sut = new SubflowTerminalRelay(gateway, MatchingRuntime(), NullLogger<SubflowTerminalRelay>.Instance);

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
        var sut = new SubflowTerminalRelay(gateway, MatchingRuntime(), NullLogger<SubflowTerminalRelay>.Instance);

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

    [Fact]
    public async Task Ignores_NonTerminal_Events()
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        var sut = new SubflowTerminalRelay(gateway, MatchingRuntime(), NullLogger<SubflowTerminalRelay>.Instance);

        var evt = new InstanceSubStateChangedEvent
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

        await sut.RelayAsync([Envelope(evt)], CancellationToken.None);

        gateway.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Gateway_Failure_Is_Swallowed_And_Logged()
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        gateway.CompleteAsync(Arg.Any<FlowCompletedInput>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result>>(_ => throw new InvalidOperationException("boom"));
        var sut = new SubflowTerminalRelay(gateway, MatchingRuntime(), NullLogger<SubflowTerminalRelay>.Instance);

        var evt = CompletedEvent();
        await Should.NotThrowAsync(() => sut.RelayAsync([Envelope(evt)], CancellationToken.None));
    }

    [Fact]
    public async Task Gateway_ResultFail_Is_Swallowed_And_Logged()
    {
        var gateway = Substitute.For<IInstanceCommandGateway>();
        gateway.CompleteAsync(Arg.Any<FlowCompletedInput>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail(Error.Failure("test.failure", "nope")));
        var sut = new SubflowTerminalRelay(gateway, MatchingRuntime(), NullLogger<SubflowTerminalRelay>.Instance);

        var evt = CompletedEvent();
        await Should.NotThrowAsync(() => sut.RelayAsync([Envelope(evt)], CancellationToken.None));

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
        var sutLocal = new SubflowTerminalRelay(gateway, localRuntime, NullLogger<SubflowTerminalRelay>.Instance);
        await sutLocal.RelayAsync([Envelope(CompletedEvent())], CancellationToken.None);

        var remoteRuntime = Substitute.For<IRuntimeInfoProvider>();
        remoteRuntime.IsDomainMatch("orders").Returns(false);
        var sutRemote = new SubflowTerminalRelay(gateway, remoteRuntime, NullLogger<SubflowTerminalRelay>.Instance);
        await sutRemote.RelayAsync([Envelope(CompletedEvent())], CancellationToken.None);

        var spans = _collected.Where(a => a.DisplayName == "Subflow.TerminalRelay").ToList();
        spans.Count.ShouldBe(2);
        spans[0].GetTagItem(TelemetryConstants.TagNames.RelayRoute).ShouldBe("local");
        spans[1].GetTagItem(TelemetryConstants.TagNames.RelayRoute).ShouldBe("remote");
    }
}
