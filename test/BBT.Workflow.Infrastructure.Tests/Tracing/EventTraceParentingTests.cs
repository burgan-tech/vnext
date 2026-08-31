using System.Diagnostics;
using System.Linq;
using BBT.Workflow.Workers.Inbox.Tracing;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Tracing;

/// <summary>
/// Pins <see cref="EventTraceParenting.ResolveParenting"/> — the pure parenting decision behind
/// <c>EventTraceScope</c>'s command/fact split — since the Inbox worker has no test project of its
/// own and the rest of the mode split (forcing/restoring a root <see cref="Activity"/>, per-event
/// identity tags) is otherwise only pinned by Faz C's OpenObserve acceptance checks.
/// </summary>
public class EventTraceParentingTests
{
    private static ActivityContext MakeContext(string traceId, string spanId, string? traceState = null) =>
        new(
            ActivityTraceId.CreateFromString(traceId),
            ActivitySpanId.CreateFromString(spanId),
            ActivityTraceFlags.Recorded,
            traceState,
            isRemote: true);

    private const string OriginTraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string OriginSpanId = "b7ad6b7169203331";
    private const string AmbientTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string AmbientSpanId = "00f067aa0ba902b7";

    private static string TraceParentFor(string traceId, string spanId) => $"00-{traceId}-{spanId}-01";

    // ----- ContinueTrace: must reproduce today's pre-split behavior exactly -----

    [Fact]
    public void ContinueTrace_WithValidTraceParent_AndNoAmbient_ParentsOntoOrigin_NoLinks()
    {
        var traceParent = TraceParentFor(OriginTraceId, OriginSpanId);

        var (parent, links) = EventTraceParenting.ResolveParenting(
            EventTraceMode.ContinueTrace, traceParent, traceState: null, ambient: default);

        parent.TraceId.ToString().ShouldBe(OriginTraceId);
        parent.SpanId.ToString().ShouldBe(OriginSpanId);
        links.ShouldBeNull();
    }

    [Fact]
    public void ContinueTrace_WithValidTraceParent_AndSameTraceAmbient_NoLinks()
    {
        var traceParent = TraceParentFor(OriginTraceId, OriginSpanId);
        var ambient = MakeContext(OriginTraceId, AmbientSpanId);

        var (parent, links) = EventTraceParenting.ResolveParenting(
            EventTraceMode.ContinueTrace, traceParent, traceState: null, ambient);

        parent.TraceId.ToString().ShouldBe(OriginTraceId);
        links.ShouldBeNull();
    }

    [Fact]
    public void ContinueTrace_WithValidTraceParent_AndMismatchedAmbient_LinksAmbientOnly()
    {
        var traceParent = TraceParentFor(OriginTraceId, OriginSpanId);
        var ambient = MakeContext(AmbientTraceId, AmbientSpanId);

        var (parent, links) = EventTraceParenting.ResolveParenting(
            EventTraceMode.ContinueTrace, traceParent, traceState: null, ambient);

        parent.TraceId.ToString().ShouldBe(OriginTraceId);
        var linkList = links.ShouldNotBeNull().ToList();
        linkList.Count.ShouldBe(1);
        linkList[0].Context.TraceId.ToString().ShouldBe(AmbientTraceId);
    }

    [Fact]
    public void ContinueTrace_WithNoTraceParent_AndAmbientPresent_ParentsOntoAmbient()
    {
        var ambient = MakeContext(AmbientTraceId, AmbientSpanId);

        var (parent, links) = EventTraceParenting.ResolveParenting(
            EventTraceMode.ContinueTrace, traceParent: null, traceState: null, ambient);

        parent.ShouldBe(ambient);
        links.ShouldBeNull();
    }

    [Fact]
    public void ContinueTrace_WithNoTraceParent_AndNoAmbient_ParentsOntoDefault()
    {
        var (parent, links) = EventTraceParenting.ResolveParenting(
            EventTraceMode.ContinueTrace, traceParent: null, traceState: null, ambient: default);

        parent.ShouldBe(default);
        links.ShouldBeNull();
    }

    // ----- LinkedDelivery: always roots; producer + ambient become links, tracestate rides the link -----

    [Fact]
    public void LinkedDelivery_WithValidTraceParentAndTraceState_RootsAndLinksOriginWithTraceState()
    {
        var traceParent = TraceParentFor(OriginTraceId, OriginSpanId);
        const string traceState = "vendor=value";

        var (parent, links) = EventTraceParenting.ResolveParenting(
            EventTraceMode.LinkedDelivery, traceParent, traceState, ambient: default);

        parent.ShouldBe(default);
        var linkList = links.ShouldNotBeNull().ToList();
        linkList.Count.ShouldBe(1);
        linkList[0].Context.TraceId.ToString().ShouldBe(OriginTraceId);
        linkList[0].Context.TraceState.ShouldBe(traceState);
    }

    [Fact]
    public void LinkedDelivery_WithTraceParentAndAmbient_RootsAndLinksBoth()
    {
        var traceParent = TraceParentFor(OriginTraceId, OriginSpanId);
        var ambient = MakeContext(AmbientTraceId, AmbientSpanId);

        var (parent, links) = EventTraceParenting.ResolveParenting(
            EventTraceMode.LinkedDelivery, traceParent, traceState: null, ambient);

        parent.ShouldBe(default);
        var linkList = links.ShouldNotBeNull().ToList();
        linkList.Count.ShouldBe(2);
        linkList.ShouldContain(l => l.Context.TraceId.ToString() == OriginTraceId);
        linkList.ShouldContain(l => l.Context.TraceId.ToString() == AmbientTraceId);
    }

    [Fact]
    public void LinkedDelivery_WithNoTraceParent_AndAmbientPresent_RootsAndLinksAmbientOnly()
    {
        var ambient = MakeContext(AmbientTraceId, AmbientSpanId);

        var (parent, links) = EventTraceParenting.ResolveParenting(
            EventTraceMode.LinkedDelivery, traceParent: null, traceState: null, ambient);

        parent.ShouldBe(default);
        var linkList = links.ShouldNotBeNull().ToList();
        linkList.Count.ShouldBe(1);
        linkList[0].Context.TraceId.ToString().ShouldBe(AmbientTraceId);
    }

    [Fact]
    public void LinkedDelivery_WithNoTraceParent_AndNoAmbient_RootsWithNoLinks()
    {
        var (parent, links) = EventTraceParenting.ResolveParenting(
            EventTraceMode.LinkedDelivery, traceParent: null, traceState: null, ambient: default);

        parent.ShouldBe(default);
        links.ShouldBeNull();
    }
}
