using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.Tracing;
using BBT.Workflow.Events;
using BBT.Workflow.Infrastructure.EventBus;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.EventBus;

/// <summary>
/// Pins <see cref="TraceStampingDistributedEventBus"/>'s only remaining responsibility: stamp W3C
/// trace context / request id / trace-lane anchors onto traceable events at publish time, then
/// delegate to the inner bus unconditionally. There is no hook execution left to test — every
/// event, hooked or not, rides straight through to <c>_inner</c>.
/// </summary>
public sealed class TraceStampingDistributedEventBusTests
{
    [Fact]
    public async Task Publish_TraceableEventWithEmptyFields_StampsAmbientTraceAndCorrelation()
    {
        var correlationProvider = Substitute.For<ICorrelationIdProvider>();
        correlationProvider.Get().Returns("req-123");
        var (sut, _) = CreateSut(correlationProvider);

        var evt = new TraceableEvent();
        var activity = new Activity("publisher");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.TraceStateString = "vendor=state";
        activity.Start();
        try
        {
            await sut.PublishAsync(evt, useOutbox: true);
        }
        finally
        {
            activity.Stop();
            Activity.Current = null;
        }

        evt.TraceParent.ShouldBe(activity.Id);
        evt.TraceState.ShouldBe("vendor=state");
        evt.RequestId.ShouldBe("req-123");
    }

    [Fact]
    public async Task Publish_TraceableEventWithPresetFields_DoesNotOverwrite()
    {
        var correlationProvider = Substitute.For<ICorrelationIdProvider>();
        correlationProvider.Get().Returns("req-123");
        var (sut, _) = CreateSut(correlationProvider);

        var evt = new TraceableEvent
        {
            TraceParent = "00-11111111111111111111111111111111-2222222222222222-01",
            TraceState = "preset=1",
            RequestId = "preset-req"
        };

        var activity = new Activity("publisher");
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();
        try
        {
            await sut.PublishAsync(evt, useOutbox: true);
        }
        finally
        {
            activity.Stop();
            Activity.Current = null;
        }

        evt.TraceParent.ShouldBe("00-11111111111111111111111111111111-2222222222222222-01");
        evt.TraceState.ShouldBe("preset=1");
        evt.RequestId.ShouldBe("preset-req");
    }

    [Fact]
    public async Task Publish_LaneAwareEventWithNullFields_StampsFromWorkflowTraceLane()
    {
        var (sut, _) = CreateSut();
        var evt = new LaneAwareEvent();

        using (WorkflowTraceLane.Use("00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-1111111111111111-01",
                   "00-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb-2222222222222222-01"))
        {
            await sut.PublishAsync(evt, useOutbox: true);
        }

        evt.TraceRoot.ShouldBe("00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-1111111111111111-01");
        evt.ParentTraceRoot.ShouldBe("00-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb-2222222222222222-01");
    }

    [Fact]
    public async Task Publish_LaneAwareEventWithoutEpisode_StampsTheAmbientActivationEpisode()
    {
        var (sut, _) = CreateSut();
        var evt = new LaneAwareEvent();
        var episode = new ActivationEpisode(
            new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero),
            TelemetryConstants.ActivationTriggers.Manual,
            "go",
            Partial: false);

        using (WorkflowTraceLane.Use("00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-1111111111111111-01", episode: episode))
        {
            await sut.PublishAsync(evt, useOutbox: true);
        }

        // The consumer's rest point measures from the publisher's original trigger, so a parent
        // resumed by this event reports what the client actually waited for.
        evt.EpisodeStartedAt.ShouldBe(episode.StartedAt);
        evt.EpisodeTrigger.ShouldBe(TelemetryConstants.ActivationTriggers.Manual);
        evt.EpisodeTransitionKey.ShouldBe("go");
    }

    [Fact]
    public async Task Publish_LaneAwareEventWithPresetEpisode_DoesNotOverwriteIt()
    {
        var (sut, _) = CreateSut();
        var preset = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var evt = new LaneAwareEvent { EpisodeStartedAt = preset, EpisodeTrigger = "event", EpisodeTransitionKey = "preset" };
        var ambient = new ActivationEpisode(DateTimeOffset.UtcNow, TelemetryConstants.ActivationTriggers.Manual, "go", false);

        using (WorkflowTraceLane.Use("00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-1111111111111111-01", episode: ambient))
        {
            await sut.PublishAsync(evt, useOutbox: true);
        }

        evt.EpisodeStartedAt.ShouldBe(preset);
        evt.EpisodeTrigger.ShouldBe("event");
        evt.EpisodeTransitionKey.ShouldBe("preset");
    }

    [Fact]
    public async Task Publish_LaneAwareEventWithPresetFields_DoesNotOverwriteLaneAnchors()
    {
        var (sut, _) = CreateSut();
        var evt = new LaneAwareEvent
        {
            TraceRoot = "00-preset-root-0000000000000000-01",
            ParentTraceRoot = "00-preset-parent-000000000000-01"
        };

        using (WorkflowTraceLane.Use("00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-1111111111111111-01",
                   "00-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb-2222222222222222-01"))
        {
            await sut.PublishAsync(evt, useOutbox: true);
        }

        evt.TraceRoot.ShouldBe("00-preset-root-0000000000000000-01");
        evt.ParentTraceRoot.ShouldBe("00-preset-parent-000000000000-01");
    }

    [Fact]
    public async Task PublishAsync_ThreeArgOverload_DelegatesToInnerWithUseOutboxTrue()
    {
        // The IEventBus-shaped (TEvent, string?, CancellationToken) overload always forwards
        // useOutbox: true to the 4-arg overload, which in turn forwards to _inner.
        var (sut, inner) = CreateSut();
        var evt = new TraceableEvent();

        await sut.PublishAsync(evt, "subject-1", CancellationToken.None);

        await inner.Received(1).PublishAsync(evt, "subject-1", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_FourArgOverload_DelegatesToInnerWithIdenticalArguments()
    {
        var (sut, inner) = CreateSut();
        var evt = new TraceableEvent();
        using var cts = new CancellationTokenSource();

        await sut.PublishAsync(evt, "subject-2", useOutbox: false, cts.Token);

        await inner.Received(1).PublishAsync(evt, "subject-2", false, cts.Token);
    }

    [Fact]
    public async Task PublishAsync_MetadataOverload_StampsAndDelegatesToInnerWithSamePayload()
    {
        var (sut, inner) = CreateSut();
        var evt = Substitute.For<IDistributedEvent, ITraceableDistributedEvent>();
        var metadata = new EventMetadata(evt.GetType(), "test.event", 1, null, null, null);

        await sut.PublishAsync(evt, metadata, "subject-3", useOutbox: true);

        await inner.Received(1).PublishAsync(evt, metadata, "subject-3", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishEnvelopeAsync_DelegatesToInner_WithoutStamping()
    {
        var (sut, inner) = CreateSut();
        var bytes = new byte[] { 1, 2, 3 };
        using var cts = new CancellationTokenSource();

        await sut.PublishEnvelopeAsync(bytes, "topic-1", "pubsub-1", cts.Token);

        await inner.Received(1).PublishEnvelopeAsync(bytes, "topic-1", "pubsub-1", cts.Token);
    }

    [Fact]
    public async Task Publish_PlainEventWithoutTraceableInterface_PublishesStraightThroughWithoutException()
    {
        // Regression guard: a plain event with no traceable interface (and no publish-mode
        // attribute of any kind, since that concept was removed entirely) must still publish
        // straight through — no filtering, no exception, no "handled elsewhere" short-circuit.
        var (sut, inner) = CreateSut();
        var evt = new PlainEvent();

        await sut.PublishAsync(evt, useOutbox: true);

        await inner.Received(1).PublishAsync(evt, Arg.Any<string?>(), true, Arg.Any<CancellationToken>());
    }

    private static (TraceStampingDistributedEventBus Sut, IDistributedEventBus Inner) CreateSut(
        ICorrelationIdProvider? correlationIdProvider = null)
    {
        var inner = Substitute.For<IDistributedEventBus>();
        var sut = new TraceStampingDistributedEventBus(
            inner,
            NullLogger<TraceStampingDistributedEventBus>.Instance,
            correlationIdProvider);
        return (sut, inner);
    }

    private sealed class TraceableEvent : ITraceableDistributedEvent
    {
        public string? TraceParent { get; set; }
        public string? TraceState { get; set; }
        public string? RequestId { get; set; }
    }

    private sealed class LaneAwareEvent : ILaneAwareDistributedEvent
    {
        public string? TraceParent { get; set; }
        public string? TraceState { get; set; }
        public string? RequestId { get; set; }
        public string? TraceRoot { get; set; }
        public string? ParentTraceRoot { get; set; }
        public DateTimeOffset? EpisodeStartedAt { get; set; }
        public string? EpisodeTrigger { get; set; }
        public string? EpisodeTransitionKey { get; set; }
    }

    private sealed class PlainEvent;
}
