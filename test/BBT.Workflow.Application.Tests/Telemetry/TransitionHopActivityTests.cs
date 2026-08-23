using System;
using System.Diagnostics;
using System.Linq;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Shared;
using BBT.Workflow.Telemetry;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins the inline chain hop span. Inline mode removes the per-hop background job; these tests
/// exist so it does not also remove the per-hop SPAN, which is what dashboards, per-hop timing and
/// chain causality are built on.
/// <para>
/// The headline cases are <see cref="Consecutive_inline_hops_are_siblings_not_nested"/> — the whole
/// point of the lane — and <see cref="Hop_carries_the_same_identity_tags_as_the_job_path"/>.
/// </para>
/// </summary>
public sealed class TransitionHopActivityTests : IDisposable
{
    // The real source the helper starts on. Listening to it by name is deliberate: a test-local
    // source would pass even if the helper started spans on an ActivitySource that is not
    // registered in Telemetry:Tracing:AdditionalSources and therefore never exported.
    private const string SourceName = "BBT.Workflow.Pipeline";

    private readonly ActivityListener _listener;

    public TransitionHopActivityTests()
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
        Activity.Current = null;
    }

    private const string TraceId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AnchorSpanId = "1111111111111111";
    private const string PredecessorSpanId = "2222222222222222";

    private static string TraceParent(string spanId) => $"00-{TraceId}-{spanId}-01";

    [Fact]
    public void Hop_is_parented_to_the_lane_anchor_and_links_the_predecessor()
    {
        // Exactly the shape BackgroundJobActivityHelper.StartFlatLaneActivity produced when this
        // hop was its own job: anchor parents, predecessor links.
        using var lane = WorkflowTraceLane.Reset(TraceParent(AnchorSpanId));

        using var hop = TransitionHopActivity.Start(
            CreateContext(), laneSeq: 3, TraceParent(PredecessorSpanId), predecessorTraceState: null);

        hop.ShouldNotBeNull();
        hop!.TraceId.ToString().ShouldBe(TraceId);
        hop.ParentSpanId.ToString().ShouldBe(AnchorSpanId);
        hop.Links.Select(l => l.Context.SpanId.ToString()).ShouldContain(PredecessorSpanId);
        hop.GetTagItem(TelemetryConstants.TagNames.TraceLane).ShouldBe(true);
        hop.GetTagItem(TelemetryConstants.TagNames.HopPredecessor).ShouldBe(PredecessorSpanId);
    }

    [Fact]
    public void Consecutive_inline_hops_are_siblings_not_nested()
    {
        // Nesting here would make trace depth equal chain depth, which is the exact failure the
        // lane model exists to prevent — and the one an inline chain is most at risk of.
        using var lane = WorkflowTraceLane.Reset(TraceParent(AnchorSpanId));

        string? predecessor;
        using (var hop1 = TransitionHopActivity.Start(
                   CreateContext(), laneSeq: 1, TraceParent(PredecessorSpanId), null))
        {
            hop1.ShouldNotBeNull();
            predecessor = hop1!.Id;
        }

        using var hop2 = TransitionHopActivity.Start(CreateContext(), laneSeq: 2, predecessor, null);

        hop2.ShouldNotBeNull();
        hop2!.ParentSpanId.ToString().ShouldBe(AnchorSpanId);
        hop2.Links.Select(l => l.Context.SpanId.ToString()).ShouldContain(predecessor![36..52]);
    }

    [Fact]
    public void Hop_carries_the_same_identity_tags_as_the_job_path()
    {
        using var lane = WorkflowTraceLane.Reset(TraceParent(AnchorSpanId));
        var context = CreateContext(chainDepth: 4);

        using var hop = TransitionHopActivity.Start(context, laneSeq: 7, TraceParent(PredecessorSpanId), null);

        hop.ShouldNotBeNull();
        hop!.GetTagItem(TelemetryConstants.TagNames.Domain).ShouldBe(context.Domain);
        hop.GetTagItem(TelemetryConstants.TagNames.Flow).ShouldBe(context.WorkflowKey);
        hop.GetTagItem(TelemetryConstants.TagNames.FlowVersion).ShouldBe(context.Workflow.Version);
        hop.GetTagItem(TelemetryConstants.TagNames.InstanceId).ShouldBe(context.InstanceId);
        hop.GetTagItem(TelemetryConstants.TagNames.TransitionKey).ShouldBe(context.TransitionKey);

        // LaneSeq is the reliable ordinal: ChainDepth resets to 0 at resume/timeout/retry
        // boundaries, so a lane cannot be ordered by it.
        hop.GetTagItem(TelemetryConstants.TagNames.ChainDepth).ShouldBe(4);
        hop.GetTagItem(TelemetryConstants.TagNames.LaneSeq).ShouldBe(7);
    }

    [Fact]
    public void Hop_kind_is_Consumer_so_apm_still_counts_it_as_a_transaction()
    {
        // apm-server classifies transactions by SpanKind. Internal here would silently drop every
        // chained transition out of transaction counts built while these hops were jobs.
        using var lane = WorkflowTraceLane.Reset(TraceParent(AnchorSpanId));

        using var hop = TransitionHopActivity.Start(CreateContext(), laneSeq: 1, null, null);

        hop.ShouldNotBeNull();
        hop!.Kind.ShouldBe(ActivityKind.Consumer);
    }

    [Fact]
    public void Hop_name_is_distinct_from_the_job_span()
    {
        // "How many transition JOBS ran" must stay answerable from traces, so an inline hop is
        // never named TransitionJob.Execute.
        TransitionHopActivity.ActivityName.ShouldBe("Transition.Hop");
        TransitionHopActivity.ActivityName.ShouldNotBe("TransitionJob.Execute");
    }

    [Fact]
    public void Without_a_lane_the_hop_degrades_to_the_predecessor_as_parent()
    {
        // Pre-lane behaviour, byte for byte: no anchor means today's parent, not a dropped span.
        using var lane = WorkflowTraceLane.Reset(anchor: null);

        using var hop = TransitionHopActivity.Start(
            CreateContext(), laneSeq: 1, TraceParent(PredecessorSpanId), null);

        hop.ShouldNotBeNull();
        hop!.ParentSpanId.ToString().ShouldBe(PredecessorSpanId);
        hop.GetTagItem(TelemetryConstants.TagNames.TraceLane).ShouldBe(false);
    }

    private static TransitionExecutionContext CreateContext(int chainDepth = 0)
    {
        var instanceId = Guid.NewGuid();
        const string workflowKey = "test-workflow";
        const string domain = "test-domain";

        var workflow = CreateMockWorkflow(workflowKey, domain);

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = "test-transition",
            Trigger = TriggerType.Automatic,
            Actor = ExecutionActor.System,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            ChainDepth = chainDepth,
            Workflow = workflow,
            Current = workflow.GetState("state1").Value!,
            Transition = Transition.Create("test-transition", null, "state1", TriggerType.Automatic, "Patch"),
            Instance = Instance.Create(instanceId, workflowKey, "1.0.0"),
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static Definitions.Workflow CreateMockWorkflow(string key, string domain)
    {
        const string json = """
        {
            "type": "F",
            "timeout": null,
            "labels": [],
            "functions": [],
            "features": [],
            "states": [
                {
                    "key": "state1",
                    "type": "P",
                    "transitions": []
                }
            ],
            "sharedTransitions": [],
            "extensions": [],
            "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
        }
        """;

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference(key, domain, "sys-flows", "1.0.0"));
        return workflow;
    }
}
