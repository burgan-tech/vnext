using System;
using System.Diagnostics;
using System.Linq;
using BBT.Workflow.Logging;
using BBT.Workflow.Telemetry;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins the flat-lane parenting policy. The headline case is
/// <see cref="Two_consecutive_hops_sharing_an_anchor_are_siblings"/>: before this work, hop N+1 was a
/// CHILD of hop N, so trace depth equalled chain depth.
/// </summary>
public sealed class FlatLaneActivityTests : IDisposable
{
    private const string SourceName = "BBT.Workflow.Tests.FlatLane";

    private readonly ActivitySource _source = new(SourceName);
    private readonly ActivityListener _listener;

    public FlatLaneActivityTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
        Activity.Current = null;
    }

    private static string TraceParent(string traceId, string spanId, string flags = "01")
        => $"00-{traceId}-{spanId}-{flags}";

    private const string TraceId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherTraceId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly string Anchor = TraceParent(TraceId, "1111111111111111");
    private static readonly string Predecessor = TraceParent(TraceId, "2222222222222222");

    [Fact]
    public void Anchor_becomes_the_parent_and_the_predecessor_is_linked()
    {
        using var activity = FlatLaneActivity.Start(
            _source, "TransitionJob.Execute", ActivityKind.Consumer, Anchor, Predecessor, traceState: null);

        activity.ShouldNotBeNull();
        activity!.TraceId.ToString().ShouldBe(TraceId);
        activity.ParentSpanId.ToString().ShouldBe("1111111111111111");

        activity.Links.Select(l => l.Context.SpanId.ToString()).ShouldContain("2222222222222222");
        activity.GetTagItem(TelemetryConstants.TagNames.TraceLane).ShouldBe(true);
        activity.GetTagItem(TelemetryConstants.TagNames.TraceLaneAnchor).ShouldBe("1111111111111111");
        activity.GetTagItem(TelemetryConstants.TagNames.HopPredecessor).ShouldBe("2222222222222222");
    }

    [Fact]
    public void Two_consecutive_hops_sharing_an_anchor_are_siblings()
    {
        using var hop1 = FlatLaneActivity.Start(
            _source, "TransitionJob.Execute", ActivityKind.Consumer, Anchor, predecessorTraceParent: null, traceState: null);
        hop1.ShouldNotBeNull();

        // Hop 2 is enqueued from inside hop 1: hop 1 is its predecessor, the anchor is unchanged.
        using var hop2 = FlatLaneActivity.Start(
            _source, "TransitionJob.Execute", ActivityKind.Consumer, Anchor, hop1!.Id, traceState: null);
        hop2.ShouldNotBeNull();

        hop2!.ParentSpanId.ShouldBe(hop1.ParentSpanId);
        hop2.ParentSpanId.ToString().ShouldBe("1111111111111111");
        hop2.ParentSpanId.ShouldNotBe(hop1.SpanId);

        // Causality survives as a link + tag rather than as a parent edge.
        hop2.Links.Select(l => l.Context.SpanId).ShouldContain(hop1.SpanId);
        hop2.GetTagItem(TelemetryConstants.TagNames.HopPredecessor).ShouldBe(hop1.SpanId.ToString());
    }

    [Fact]
    public void First_hop_of_a_lane_does_not_link_the_anchor_to_itself()
    {
        using var activity = FlatLaneActivity.Start(
            _source, "TransitionJob.Execute", ActivityKind.Consumer, Anchor, Anchor, traceState: null);

        activity.ShouldNotBeNull();
        activity!.Links.ShouldBeEmpty();
    }

    [Fact]
    public void Without_an_anchor_the_predecessor_stays_the_parent()
    {
        using var activity = FlatLaneActivity.Start(
            _source, "TransitionJob.Execute", ActivityKind.Consumer,
            anchorTraceParent: null, predecessorTraceParent: Predecessor, traceState: null);

        activity.ShouldNotBeNull();
        activity!.ParentSpanId.ToString().ShouldBe("2222222222222222");
        activity.GetTagItem(TelemetryConstants.TagNames.TraceLane).ShouldBe(false);
        activity.GetTagItem(TelemetryConstants.TagNames.TraceLaneMismatch).ShouldBeNull();
    }

    [Fact]
    public void A_malformed_anchor_degrades_to_the_predecessor_without_throwing()
    {
        using var activity = FlatLaneActivity.Start(
            _source, "TransitionJob.Execute", ActivityKind.Consumer, "not-a-traceparent", Predecessor, traceState: null);

        activity.ShouldNotBeNull();
        activity!.ParentSpanId.ToString().ShouldBe("2222222222222222");
        activity.GetTagItem(TelemetryConstants.TagNames.TraceLane).ShouldBe(false);
    }

    [Fact]
    public void An_anchor_from_another_trace_is_linked_but_never_parented()
    {
        var foreignAnchor = TraceParent(OtherTraceId, "3333333333333333");

        using var activity = FlatLaneActivity.Start(
            _source, "TransitionJob.Execute", ActivityKind.Consumer, foreignAnchor, Predecessor, traceState: null);

        activity.ShouldNotBeNull();
        // Stays in the predecessor's trace — a stale or forged anchor cannot teleport the span.
        activity!.TraceId.ToString().ShouldBe(TraceId);
        activity.ParentSpanId.ToString().ShouldBe("2222222222222222");
        activity.GetTagItem(TelemetryConstants.TagNames.TraceLaneMismatch).ShouldBe(true);
        activity.GetTagItem(TelemetryConstants.TagNames.TraceLane).ShouldBe(false);
    }

    [Fact]
    public void Consumer_kind_is_preserved_so_APM_still_classifies_the_span_as_a_transaction()
    {
        using var activity = FlatLaneActivity.Start(
            _source, "TransitionJob.Execute", ActivityKind.Consumer, Anchor, Predecessor, traceState: null);

        activity.ShouldNotBeNull();
        activity!.Kind.ShouldBe(ActivityKind.Consumer);
    }

    [Fact]
    public void Internal_kind_is_preserved_for_in_process_lane_items()
    {
        using var activity = FlatLaneActivity.Start(
            _source, "PostCommit.ForwardToSubflowJob", ActivityKind.Internal, Anchor, Predecessor, traceState: null);

        activity.ShouldNotBeNull();
        activity!.Kind.ShouldBe(ActivityKind.Internal);
    }

    [Fact]
    public void An_ambient_span_from_another_trace_is_demoted_to_a_link()
    {
        var ambient = new Activity("dapr-callback");
        ambient.SetIdFormat(ActivityIdFormat.W3C);
        ambient.Start();
        try
        {
            using var activity = FlatLaneActivity.Start(
                _source, "TransitionJob.Execute", ActivityKind.Consumer, Anchor, Predecessor, traceState: null);

            activity.ShouldNotBeNull();
            activity!.ParentSpanId.ToString().ShouldBe("1111111111111111");
            activity.Links.Select(l => l.Context.SpanId).ShouldContain(ambient.SpanId);
            activity.GetTagItem(TelemetryConstants.TagNames.DaprCallback).ShouldBe(true);
        }
        finally
        {
            ambient.Stop();
            Activity.Current = null;
        }
    }

    [Fact]
    public void The_anchor_is_read_from_the_ambient_lane_when_not_passed_explicitly()
    {
        using (WorkflowTraceLane.Use(Anchor))
        {
            using var activity = FlatLaneActivity.Start(
                _source, "PostCommit.StartSubflowJob", ActivityKind.Internal,
                anchorTraceParent: null, predecessorTraceParent: Predecessor, traceState: null);

            activity.ShouldNotBeNull();
            activity!.ParentSpanId.ToString().ShouldBe("1111111111111111");
            activity.GetTagItem(TelemetryConstants.TagNames.TraceLane).ShouldBe(true);
        }
    }

    [Fact]
    public void Tracestate_rides_along_to_the_lane_span()
    {
        using var activity = FlatLaneActivity.Start(
            _source, "TransitionJob.Execute", ActivityKind.Consumer, Anchor, Predecessor, traceState: "vendor=x");

        activity.ShouldNotBeNull();
        activity!.TraceStateString.ShouldBe("vendor=x");
    }

    [Fact]
    public void An_unsampled_anchor_produces_an_unsampled_lane_span()
    {
        // Head sampling must be inherited from the anchor, otherwise a whole business request could
        // be dropped upstream while its lane spans are still recorded (or vice versa).
        using var parentBased = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BBT.Workflow.Tests.FlatLane.ParentBased",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.Parent.TraceFlags.HasFlag(ActivityTraceFlags.Recorded)
                    ? ActivitySamplingResult.AllDataAndRecorded
                    : ActivitySamplingResult.PropagationData
        };
        ActivitySource.AddActivityListener(parentBased);

        using var source = new ActivitySource("BBT.Workflow.Tests.FlatLane.ParentBased");
        var unsampledAnchor = TraceParent(TraceId, "1111111111111111", flags: "00");

        using var activity = FlatLaneActivity.Start(
            source, "TransitionJob.Execute", ActivityKind.Consumer, unsampledAnchor, predecessorTraceParent: null, traceState: null);

        activity.ShouldNotBeNull();
        activity!.Recorded.ShouldBeFalse();
    }

    [Fact]
    public void A_foreign_ambient_span_does_not_disqualify_the_anchor_when_there_is_no_predecessor()
    {
        // The job path: the ambient span is the Dapr scheduler callback, which is its OWN trace by
        // construction. Treating that as an anchor/trace mismatch would reject every legitimate
        // anchor whenever the payload happens to carry no predecessor.
        var ambient = new Activity("dapr-callback");
        ambient.SetIdFormat(ActivityIdFormat.W3C);
        ambient.Start();
        try
        {
            using var activity = FlatLaneActivity.Start(
                _source, "TransitionJob.Execute", ActivityKind.Consumer,
                Anchor, predecessorTraceParent: null, traceState: null);

            activity.ShouldNotBeNull();
            activity!.ParentSpanId.ToString().ShouldBe("1111111111111111");
            activity.GetTagItem(TelemetryConstants.TagNames.TraceLane).ShouldBe(true);
            activity.GetTagItem(TelemetryConstants.TagNames.TraceLaneMismatch).ShouldBeNull();
            // The callback is not lost — it is linked.
            activity.Links.Select(l => l.Context.SpanId).ShouldContain(ambient.SpanId);
            activity.GetTagItem(TelemetryConstants.TagNames.DaprCallback).ShouldBe(true);
        }
        finally
        {
            ambient.Stop();
            Activity.Current = null;
        }
    }
}
