using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BBT.Workflow.HttpApi.Shared.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins the idle-poll suppression: only a ROOT <c>Db.*</c> span is dropped. A child <c>Db.*</c>
/// span (real work under a rooted episode like <c>Outbox.Process</c>) and any non-<c>Db.</c> root
/// must survive untouched.
/// </summary>
public sealed class IdlePollSpanProcessorTests
{
    private const string SourceName = "BBT.Workflow.Tests.IdlePollSpanProcessor";

    /// <summary>
    /// Runs the processor through a real tracer provider (as the SDK does) and collects the
    /// finished activities so assertions see the <see cref="ActivityTraceFlags"/> spans were
    /// exported with.
    /// </summary>
    private static (List<Activity> Exported, TracerProvider Provider, ActivitySource Source) CreateHarness()
    {
        var exported = new List<Activity>();
        var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(SourceName)
            .SetSampler(new AlwaysOnSampler())
            .AddProcessor(new IdlePollSpanProcessor())
            .AddProcessor(new CapturingProcessor(exported))
            .Build()!;

        return (exported, provider, new ActivitySource(SourceName));
    }

    private sealed class CapturingProcessor(List<Activity> sink) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => sink.Add(data);
    }

    [Fact]
    public void RootDbSelectSpan_IsDropped()
    {
        var (exported, provider, source) = CreateHarness();
        using (provider)
        using (source)
        {
            using (source.StartActivity("Db.SELECT"))
            {
            }

            provider.ForceFlush();
        }

        exported.ShouldHaveSingleItem();
        (exported[0].ActivityTraceFlags & ActivityTraceFlags.Recorded).ShouldBe(default);
    }

    [Fact]
    public void ChildDbSelectSpan_UnderARootedEpisode_Survives()
    {
        var (exported, provider, source) = CreateHarness();
        using (provider)
        using (source)
        {
            using (source.StartActivity("Outbox.Process"))
            {
                using (source.StartActivity("Db.SELECT"))
                {
                }
            }

            provider.ForceFlush();
        }

        exported.Count.ShouldBe(2);
        var child = exported.Single(a => a.DisplayName == "Db.SELECT");
        (child.ActivityTraceFlags & ActivityTraceFlags.Recorded).ShouldBe(ActivityTraceFlags.Recorded);
    }

    [Fact]
    public void RootNonDbSpan_Survives()
    {
        var (exported, provider, source) = CreateHarness();
        using (provider)
        using (source)
        {
            using (source.StartActivity("Outbox.Process"))
            {
            }

            provider.ForceFlush();
        }

        exported.ShouldHaveSingleItem();
        (exported[0].ActivityTraceFlags & ActivityTraceFlags.Recorded).ShouldBe(ActivityTraceFlags.Recorded);
    }

    [Fact]
    public void RootDbSpan_IsDroppedRegardlessOfVerb()
    {
        var (exported, provider, source) = CreateHarness();
        using (provider)
        using (source)
        {
            using (source.StartActivity("Db.INSERT"))
            {
            }

            provider.ForceFlush();
        }

        exported.ShouldHaveSingleItem();
        (exported[0].ActivityTraceFlags & ActivityTraceFlags.Recorded).ShouldBe(default);
    }
}
