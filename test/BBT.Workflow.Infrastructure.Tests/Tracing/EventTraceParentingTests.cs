using System.Diagnostics;
using BBT.Workflow.Workers.Inbox.Tracing;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Tracing;

/// <summary>
/// Pins <see cref="EventTraceParenting.ResolveParent"/> — the pure parenting decision behind
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

    // ----- ContinueTrace: continue the origin without cross-linking transport traces -----

    [Fact]
    public void ContinueTrace_WithValidTraceParent_AndNoAmbient_ParentsOntoOrigin_NoLinks()
    {
        var traceParent = TraceParentFor(OriginTraceId, OriginSpanId);

        var parent = EventTraceParenting.ResolveParent(
            EventTraceMode.ContinueTrace, traceParent, traceState: null, ambient: default);

        parent.TraceId.ToString().ShouldBe(OriginTraceId);
        parent.SpanId.ToString().ShouldBe(OriginSpanId);
    }

    [Fact]
    public void ContinueTrace_WithValidTraceParent_AndSameTraceAmbient_NoLinks()
    {
        var traceParent = TraceParentFor(OriginTraceId, OriginSpanId);
        var ambient = MakeContext(OriginTraceId, AmbientSpanId);

        var parent = EventTraceParenting.ResolveParent(
            EventTraceMode.ContinueTrace, traceParent, traceState: null, ambient);

        parent.TraceId.ToString().ShouldBe(OriginTraceId);
    }

    [Fact]
    public void ContinueTrace_WithValidTraceParent_AndMismatchedAmbient_DoesNotCrossLinkTransport()
    {
        var traceParent = TraceParentFor(OriginTraceId, OriginSpanId);
        var ambient = MakeContext(AmbientTraceId, AmbientSpanId);

        var parent = EventTraceParenting.ResolveParent(
            EventTraceMode.ContinueTrace, traceParent, traceState: null, ambient);

        parent.TraceId.ToString().ShouldBe(OriginTraceId);
    }

    [Fact]
    public void ContinueTrace_WithNoTraceParent_AndAmbientPresent_ParentsOntoAmbient()
    {
        var ambient = MakeContext(AmbientTraceId, AmbientSpanId);

        var parent = EventTraceParenting.ResolveParent(
            EventTraceMode.ContinueTrace, traceParent: null, traceState: null, ambient);

        parent.ShouldBe(ambient);
    }

    [Fact]
    public void ContinueTrace_WithNoTraceParent_AndNoAmbient_ParentsOntoDefault()
    {
        var parent = EventTraceParenting.ResolveParent(
            EventTraceMode.ContinueTrace, traceParent: null, traceState: null, ambient: default);

        parent.ShouldBe(default);
    }

    // ----- IsolatedDelivery: always roots; producer + ambient are not linked -----

    [Fact]
    public void IsolatedDelivery_WithValidTraceParentAndTraceState_RootsWithoutLinks()
    {
        var traceParent = TraceParentFor(OriginTraceId, OriginSpanId);
        const string traceState = "vendor=value";

        var parent = EventTraceParenting.ResolveParent(
            EventTraceMode.IsolatedDelivery, traceParent, traceState, ambient: default);

        parent.ShouldBe(default);
    }

    [Fact]
    public void IsolatedDelivery_WithTraceParentAndAmbient_RootsWithoutLinks()
    {
        var traceParent = TraceParentFor(OriginTraceId, OriginSpanId);
        var ambient = MakeContext(AmbientTraceId, AmbientSpanId);

        var parent = EventTraceParenting.ResolveParent(
            EventTraceMode.IsolatedDelivery, traceParent, traceState: null, ambient);

        parent.ShouldBe(default);
    }

    [Fact]
    public void IsolatedDelivery_WithNoTraceParent_AndAmbientPresent_RootsWithoutLinks()
    {
        var ambient = MakeContext(AmbientTraceId, AmbientSpanId);

        var parent = EventTraceParenting.ResolveParent(
            EventTraceMode.IsolatedDelivery, traceParent: null, traceState: null, ambient);

        parent.ShouldBe(default);
    }

    [Fact]
    public void IsolatedDelivery_WithNoTraceParent_AndNoAmbient_RootsWithNoLinks()
    {
        var parent = EventTraceParenting.ResolveParent(
            EventTraceMode.IsolatedDelivery, traceParent: null, traceState: null, ambient: default);

        parent.ShouldBe(default);
    }
}
