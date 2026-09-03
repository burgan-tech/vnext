using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BBT.Workflow.Logging;
using BBT.Workflow.Telemetry;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins the synthetic, backdated <c>Instance.Activation/{key}</c> span: its start is the carried
/// episode start (not "now"), its parent is the lane anchor, the settling span is linked, and —
/// the trap every explicit-parent span in this codebase must avoid — emitting it leaves the
/// caller's <see cref="Activity.Current"/> exactly as it was.
/// </summary>
[Collection(TracingDetailLevelCollection.Name)]
public sealed class ActivationActivityTests : IDisposable
{
    // Literal, NOT PipelineStepActivityHelper.ActivitySource.Name: reading the static field inside
    // ShouldListenTo re-enters the helper's type initializer (process-poisoning NRE).
    private const string PipelineSource = "BBT.Workflow.Pipeline";
    private const string TestSource = "BBT.Workflow.Tests.Activation";

    private static readonly ActivitySource Source = new(TestSource);

    private readonly List<ActivityListener> _listeners = new();
    private readonly Guid _instanceId = Guid.NewGuid();

    public void Dispose()
    {
        foreach (var l in _listeners) l.Dispose();
        Activity.Current = null;
    }

    private List<Activity> Listen(params string[] sourceNames)
    {
        var collected = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => Array.IndexOf(sourceNames, source.Name) >= 0,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = collected.Add
        };
        ActivitySource.AddActivityListener(listener);
        _listeners.Add(listener);
        return collected;
    }

    private Activity? Emit(string outcome = TelemetryConstants.ActivationOutcomes.Active, bool casFlipped = true)
        => ActivationActivity.Emit(Source, outcome, _instanceId, "dom", "flow", "go", "waiting", casFlipped);

    [Fact]
    public void Emit_backdates_the_start_to_the_episode_and_parents_to_the_lane_anchor()
    {
        Listen(TestSource);

        // The APM transaction: it anchors the lane and seeds the episode from its own start.
        using var root = Source.StartActivity("PATCH /transitions/go")!;
        using var lane = WorkflowTraceLane.UseCurrentActivity();
        using var classify = WorkflowTraceLane.UseEpisode(TelemetryConstants.ActivationTriggers.Manual, "go");
        // The settling hop's span, current at emit time.
        using var hop = Source.StartActivity("TransitionJob.Execute/go")!;

        var emitted = Emit().ShouldNotBeNull();

        emitted.DisplayName.ShouldBe("Instance.Activation/go");
        emitted.Kind.ShouldBe(ActivityKind.Internal);
        emitted.StartTimeUtc.ShouldBe(root.StartTimeUtc);
        emitted.Duration.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
        emitted.ParentSpanId.ShouldBe(root.Context.SpanId);
        emitted.TraceId.ShouldBe(root.TraceId);
        emitted.Links.Select(l => l.Context.SpanId).ShouldContain(hop.Context.SpanId);

        emitted.GetTagItem(TelemetryConstants.TagNames.ActivationOutcome).ShouldBe(TelemetryConstants.ActivationOutcomes.Active);
        emitted.GetTagItem(TelemetryConstants.TagNames.ActivationTrigger).ShouldBe(TelemetryConstants.ActivationTriggers.Manual);
        emitted.GetTagItem(TelemetryConstants.TagNames.ActivationTransitionKey).ShouldBe("go");
        emitted.GetTagItem(TelemetryConstants.TagNames.SettleCas).ShouldBe("flipped");
        emitted.GetTagItem(TelemetryConstants.TagNames.InstanceId).ShouldBe(_instanceId.ToString());
        emitted.GetTagItem(TelemetryConstants.TagNames.StateTo).ShouldBe("waiting");
        emitted.GetTagItem(TelemetryConstants.TagNames.SpanCategory).ShouldBe(TelemetryConstants.SpanCategories.Business);
        emitted.GetTagItem(TelemetryConstants.TagNames.ActivationPartial).ShouldBeNull();
        emitted.GetTagItem(TelemetryConstants.TagNames.ActivationClockSkew).ShouldBeNull();
    }

    [Fact]
    public void Emit_restores_Activity_Current()
    {
        // Explicit parent ⇒ Activity.Parent == null ⇒ Stop() would leave Activity.Current null and
        // strip the caller's ambient span for the rest of its frame.
        Listen(TestSource);

        using var root = Source.StartActivity("root")!;
        using var lane = WorkflowTraceLane.UseCurrentActivity();
        using var hop = Source.StartActivity("hop")!;

        Emit().ShouldNotBeNull();

        Activity.Current.ShouldBeSameAs(hop);
    }

    [Fact]
    public void Emit_for_an_inherited_child_episode_parents_to_the_episode_root_not_the_later_handoff()
    {
        Listen(TestSource);

        using var request = Source.StartActivity("PATCH /transitions/to-review")!;
        using var lane = WorkflowTraceLane.UseCurrentActivity();
        using var classify = WorkflowTraceLane.UseEpisode(TelemetryConstants.ActivationTriggers.Manual, "to-review");
        using var handoff = Source.StartActivity("PostCommit.StartSubflowJob")!;
        using var childLane = WorkflowTraceLane.EnterChildLane();
        using var settlingHop = Source.StartActivity("child/create")!;

        var emitted = Emit().ShouldNotBeNull();

        emitted.StartTimeUtc.ShouldBe(request.StartTimeUtc);
        emitted.ParentSpanId.ShouldBe(request.SpanId);
        emitted.ParentSpanId.ShouldNotBe(handoff.SpanId);
        emitted.Links.Select(l => l.Context.SpanId).ShouldContain(settlingHop.SpanId);
    }

    [Fact]
    public void Emit_without_an_episode_covers_only_the_hop_and_is_tagged_partial()
    {
        Listen(TestSource);

        // No lane, no episode: the payload came from a build that predates both.
        using var hop = Source.StartActivity("TransitionJob.Execute/go")!;

        var emitted = Emit().ShouldNotBeNull();

        emitted.StartTimeUtc.ShouldBe(hop.StartTimeUtc);
        emitted.ParentSpanId.ShouldBe(hop.Context.SpanId);
        emitted.GetTagItem(TelemetryConstants.TagNames.ActivationPartial).ShouldBe(true);
        emitted.GetTagItem(TelemetryConstants.TagNames.ActivationTrigger).ShouldBe(TelemetryConstants.ActivationTriggers.Job);
    }

    [Fact]
    public void Emit_with_a_future_start_clamps_to_zero_and_tags_clock_skew()
    {
        Listen(TestSource);

        using var root = Source.StartActivity("root")!;
        var future = new ActivationEpisode(
            DateTimeOffset.UtcNow.AddMinutes(5), TelemetryConstants.ActivationTriggers.Manual, "go", Partial: false);
        using var lane = WorkflowTraceLane.Use(root.Id, episode: future);

        var emitted = Emit().ShouldNotBeNull();

        emitted.Duration.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        emitted.GetTagItem(TelemetryConstants.TagNames.ActivationClockSkew).ShouldBe(true);
    }

    [Fact]
    public void Emit_with_an_anchor_from_another_trace_falls_back_to_the_ambient_parent()
    {
        Listen(TestSource);

        const string foreignAnchor = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-1111111111111111-01";
        using var hop = Source.StartActivity("hop")!;
        using var lane = WorkflowTraceLane.Use(
            foreignAnchor,
            episode: new ActivationEpisode(DateTimeOffset.UtcNow.AddSeconds(-2), TelemetryConstants.ActivationTriggers.Manual, "go", false));

        var emitted = Emit().ShouldNotBeNull();

        emitted.TraceId.ShouldBe(hop.TraceId);
        emitted.ParentSpanId.ShouldBe(hop.Context.SpanId);
    }

    [Fact]
    public void Emit_names_the_span_after_the_settling_key_but_keeps_the_episode_key_as_a_tag()
    {
        Listen(TestSource);

        using var root = Source.StartActivity("root")!;
        using (WorkflowTraceLane.Use(root.Id, episode: new ActivationEpisode(DateTimeOffset.UtcNow, "manual", "start-it", false)))
        {
            var emitted = Emit().ShouldNotBeNull();
            emitted.DisplayName.ShouldBe("Instance.Activation/go");
            emitted.GetTagItem(TelemetryConstants.TagNames.ActivationTransitionKey).ShouldBe("start-it");
        }

        using (WorkflowTraceLane.Use(root.Id, episode: new ActivationEpisode(DateTimeOffset.UtcNow, "resume", null, false)))
        {
            Emit().ShouldNotBeNull().DisplayName.ShouldBe("Instance.Activation/go");
        }
    }

    [Fact]
    public void Emit_returns_null_and_leaves_Activity_Current_alone_when_nobody_listens()
    {
        // No listener on the test source: StartActivity yields null and nothing else may change.
        using var listenElsewhere = new ActivityListener
        {
            ShouldListenTo = s => s.Name == PipelineSource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listenElsewhere);
        Activity.Current = null;

        Emit().ShouldBeNull();
        Activity.Current.ShouldBeNull();
    }

    [Fact]
    public void Emit_records_a_non_active_outcome_without_a_cas_flip()
    {
        Listen(TestSource);

        using var root = Source.StartActivity("root")!;
        using var lane = WorkflowTraceLane.UseCurrentActivity();

        var emitted = Emit(TelemetryConstants.ActivationOutcomes.BusySubflow, casFlipped: false).ShouldNotBeNull();

        emitted.GetTagItem(TelemetryConstants.TagNames.ActivationOutcome).ShouldBe(TelemetryConstants.ActivationOutcomes.BusySubflow);
        emitted.GetTagItem(TelemetryConstants.TagNames.SettleCas).ShouldBe("n/a");
    }
}
