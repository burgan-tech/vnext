using System;
using System.Diagnostics;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Workers.Inbox.Tracing;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Tracing;

/// <summary>
/// Pins <see cref="EventTraceScope"/>'s highest-risk behavior — the ambient
/// <see cref="Activity.Current"/> clear/restore choreography that forces a genuine root span for
/// <see cref="EventTraceMode.LinkedDelivery"/> — which sits outside <c>EventTraceParenting</c>'s
/// pure decision table and so was otherwise pinned only by Faz C's OpenObserve acceptance checks.
/// Compile-included into this test project the same way as <c>EventTraceParenting.cs</c>: the Inbox
/// worker has no test project of its own, and <c>EventTraceScope.cs</c> depends only on
/// Domain/Events.Contracts/Aether types already available here.
/// </summary>
public sealed class EventTraceScopeTests
{
    private static InstanceCanceledEvent MakeEvent(string? traceParent = null, string? traceState = null) => new()
    {
        InstanceId = Guid.NewGuid(),
        Domain = "test-domain",
        Flow = "test-flow",
        Version = "1.0",
        CanceledState = "Canceled",
        CanceledAt = DateTime.UtcNow,
        TraceParent = traceParent,
        TraceState = traceState
    };

    private static ActivityListener ListenTo(string sourceName) => new()
    {
        ShouldListenTo = source => source.Name == sourceName,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
    };

    private static Activity StartAmbient(string name)
    {
        var ambient = new Activity(name);
        ambient.SetIdFormat(ActivityIdFormat.W3C);
        ambient.Start();
        return ambient;
    }

    [Fact]
    public void LinkedDelivery_WithAmbientActivity_StartsGenuineRootSpan_NotChildOfAmbient()
    {
        using var listener = ListenTo(EventTraceScope.ActivitySource.Name);
        ActivitySource.AddActivityListener(listener);

        var ambient = StartAmbient("ambient-pubsub-delivery");
        try
        {
            var evt = MakeEvent();
            using var scope = EventTraceScope.Start(
                "Test.LinkedDelivery.Handle", evt, correlationIdProvider: null,
                EventTraceMode.LinkedDelivery, messageId: "msg-1");

            // (a) A default parentContext alone would fall back to the ambient activity (the Task A1
            // gotcha, pinned separately by ActivityParentContextSemanticsTests). If EventTraceScope
            // did not clear Activity.Current first, this span would be a CHILD of `ambient` instead
            // of a genuine root: same trace id, non-default ParentSpanId.
            var started = Activity.Current;
            started.ShouldNotBeNull();
            started!.ParentSpanId.ShouldBe(default(ActivitySpanId));
            started.TraceId.ShouldNotBe(ambient.TraceId);
        }
        finally
        {
            ambient.Stop();
            Activity.Current = null;
        }
    }

    [Fact]
    public void LinkedDelivery_AfterDispose_RestoresThePriorAmbientActivity()
    {
        using var listener = ListenTo(EventTraceScope.ActivitySource.Name);
        ActivitySource.AddActivityListener(listener);

        var ambient = StartAmbient("ambient-pubsub-delivery");
        try
        {
            var evt = MakeEvent();
            var scope = EventTraceScope.Start(
                "Test.LinkedDelivery.Handle", evt, correlationIdProvider: null,
                EventTraceMode.LinkedDelivery, messageId: "msg-1");

            // Sanity: while the scope is live, the handler body sees the new root, not `ambient`.
            Activity.Current.ShouldNotBeSameAs(ambient);

            scope.Dispose();

            // (b) A forced-root span's local Parent reference is null, so Activity.Stop() (invoked by
            // Dispose) would otherwise leave Activity.Current at null instead of restoring `ambient`.
            Activity.Current.ShouldBeSameAs(ambient);
        }
        finally
        {
            ambient.Stop();
            Activity.Current = null;
        }
    }

    [Fact]
    public void LinkedDelivery_WhenSpanIsSampledOut_StillRestoresAmbientActivityOnDispose()
    {
        // Deliberately NO ActivityListener registered for EventTraceScope.ActivitySource: with no
        // listener, ActivitySource.StartActivity returns null — the "sampled out" case. The restore
        // in Dispose must not depend on the started activity being non-null.
        var ambient = StartAmbient("ambient-pubsub-delivery");
        try
        {
            var evt = MakeEvent();
            var scope = EventTraceScope.Start(
                "Test.LinkedDelivery.Handle", evt, correlationIdProvider: null,
                EventTraceMode.LinkedDelivery, messageId: "msg-1");

            // (c) Nothing was created to replace it, and Start() explicitly cleared Activity.Current
            // to force a root — so between Start() and Dispose(), Current is genuinely null.
            Activity.Current.ShouldBeNull();

            scope.Dispose();

            Activity.Current.ShouldBeSameAs(ambient);
        }
        finally
        {
            ambient.Stop();
            Activity.Current = null;
        }
    }

    [Fact]
    public void ContinueTrace_DoesNotForceARoot_ParentsOntoTheEventTraceParent()
    {
        using var listener = ListenTo(EventTraceScope.ActivitySource.Name);
        ActivitySource.AddActivityListener(listener);

        var ambient = StartAmbient("ambient-pubsub-delivery");
        try
        {
            const string originTraceId = "0af7651916cd43dd8448eb211c80319c";
            const string originSpanId = "b7ad6b7169203331";
            var evt = MakeEvent(traceParent: $"00-{originTraceId}-{originSpanId}-01");

            using var scope = EventTraceScope.Start(
                "Test.ContinueTrace.Handle", evt, correlationIdProvider: null,
                EventTraceMode.ContinueTrace, messageId: "msg-1");

            // (d) ContinueTrace never clears Activity.Current: the started span parents onto the
            // EVENT's own TraceParent (not a fresh root, and not onto `ambient` either).
            var started = Activity.Current;
            started.ShouldNotBeNull();
            started!.TraceId.ToString().ShouldBe(originTraceId);
            started.ParentSpanId.ToString().ShouldBe(originSpanId);
            started.TraceId.ShouldNotBe(ambient.TraceId);
        }
        finally
        {
            ambient.Stop();
            Activity.Current = null;
        }
    }
}
