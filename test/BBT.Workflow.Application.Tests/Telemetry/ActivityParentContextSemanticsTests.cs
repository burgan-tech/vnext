using System;
using System.Diagnostics;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins the .NET <see cref="ActivitySource.StartActivity(string, ActivityKind, ActivityContext, System.Collections.Generic.IEnumerable{System.Collections.Generic.KeyValuePair{string, object}}, System.Collections.Generic.IEnumerable{ActivityLink}, DateTimeOffset)"/>
/// semantics that the flat-lane tracing design depends on.
/// <para>
/// Passing an EXPLICIT, non-default <see cref="ActivityContext"/> makes .NET set
/// <see cref="Activity.ParentSpanId"/> only and leave <see cref="Activity.Parent"/> null. Because
/// <see cref="Activity.Baggage"/> walks the <c>Parent</c> chain, baggage is NOT visible to such a
/// child — even when the explicit context is the in-process parent's own context.
/// </para>
/// <para>
/// Consequence for this codebase: every span created through
/// <c>BackgroundJobActivityHelper</c>, <c>PostCommitExecutor</c>, <c>SubFlowActivityHelper</c> or
/// <c>PipelineStepActivityHelper</c> — all of which pass an explicit context — is already blind to
/// baggage set by <c>TransitionExecutor.EnrichTelemetry</c>. That is why the flat-lane anchor is
/// carried in the job payload / event contract rather than in W3C baggage.
/// </para>
/// </summary>
public sealed class ActivityParentContextSemanticsTests : IDisposable
{
    private const string SourceName = "BBT.Workflow.Tests.ActivitySemantics";

    private readonly ActivitySource _source = new(SourceName);
    private readonly ActivityListener _listener;

    public ActivityParentContextSemanticsTests()
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

    [Fact]
    public void ExplicitParentContext_SeversTheParentObjectLink_AndHidesBaggage()
    {
        using var parent = _source.StartActivity("parent", ActivityKind.Internal);
        parent.ShouldNotBeNull();
        parent!.SetBaggage("vnext.correlation.id", "corr-1");
        parent.GetBaggageItem("vnext.correlation.id").ShouldBe("corr-1");

        // Exactly what BackgroundJobActivityHelper / PostCommitExecutor / SubFlowActivityHelper do:
        // hand StartActivity an explicit context instead of relying on the ambient Activity.
        using var child = _source.StartActivity(
            "child-explicit-context",
            ActivityKind.Internal,
            parentContext: parent.Context);

        child.ShouldNotBeNull();

        // Trace correlation still works — the ids are right...
        child!.TraceId.ShouldBe(parent.TraceId);
        child.ParentSpanId.ShouldBe(parent.SpanId);

        // ...but the in-process object link is gone, so baggage cannot be inherited.
        child.Parent.ShouldBeNull();
        child.GetBaggageItem("vnext.correlation.id").ShouldBeNull();
        child.Baggage.ShouldBeEmpty();
    }

    [Fact]
    public void AmbientParent_KeepsTheParentObjectLink_AndInheritsBaggage()
    {
        using var parent = _source.StartActivity("parent", ActivityKind.Internal);
        parent.ShouldNotBeNull();
        parent!.SetBaggage("vnext.correlation.id", "corr-1");

        // No parentContext argument: .NET uses Activity.Current as the parent OBJECT.
        using var child = _source.StartActivity("child-ambient", ActivityKind.Internal);

        child.ShouldNotBeNull();
        child!.Parent.ShouldBeSameAs(parent);
        child.GetBaggageItem("vnext.correlation.id").ShouldBe("corr-1");
    }

    [Fact]
    public void DefaultParentContext_IsTreatedAsAmbient_AndInheritsBaggage()
    {
        using var parent = _source.StartActivity("parent", ActivityKind.Internal);
        parent.ShouldNotBeNull();
        parent!.SetBaggage("vnext.correlation.id", "corr-1");

        // `Activity.Current?.Context ?? default` collapses to default() when there is no ambient
        // span; when there IS one, passing default() (not the context) keeps the object link.
        using var child = _source.StartActivity(
            "child-default-context",
            ActivityKind.Internal,
            parentContext: default);

        child.ShouldNotBeNull();
        child!.Parent.ShouldBeSameAs(parent);
        child.GetBaggageItem("vnext.correlation.id").ShouldBe("corr-1");
    }
}
