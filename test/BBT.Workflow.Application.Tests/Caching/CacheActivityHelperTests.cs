using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BBT.Aether.Telemetry;
using BBT.Workflow.Caching;
using BBT.Workflow.Logging;
using Xunit;

// The detail level this class switches is process-global; see TracingDetailLevelCollection.

namespace BBT.Workflow.Application.Tests.Caching;

/// <summary>
/// Pins the always-on creation rule for component-cache spans (Task 10, following the Task 3/4
/// precedent set by <c>PipelineStepActivityHelper</c>): cache spans are created in Business mode
/// too, never gated behind <c>AetherTracingRuntime.IsVerbose</c>, and carry
/// <c>span.category=business</c> — not <c>diagnostic</c> — since the spec (§4/§7) treats L1/L2
/// cache visibility as always-on business telemetry, not a verbose-only diagnostic.
/// </summary>
[Collection(TracingDetailLevelCollection.Name)]
public sealed class CacheActivityHelperTests : IDisposable
{
    /// <summary>
    /// Root source for the per-test ambient activity. Cache spans inherit its trace id, which is
    /// what keeps <c>Assert.Single</c> honest: the listener below is process-wide, so without this
    /// anchor a cache span emitted by a test running concurrently in another collection lands in
    /// this test's list and the assertion fails for reasons that have nothing to do with it.
    /// </summary>
    private static readonly ActivitySource TestSource = new("CacheActivityHelperTests");

    private readonly AetherTracingDetailLevel _originalLevel = AetherTracingRuntime.DetailLevel;
    private readonly List<ActivityListener> _listeners = new();
    private readonly Activity _root;

    public CacheActivityHelperTests()
    {
        AetherTracingRuntime.Configure(AetherTracingDetailLevel.Business);

        // Registered before the root is started: an ActivitySource with no listener returns null.
        var rootListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "CacheActivityHelperTests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(rootListener);
        _listeners.Add(rootListener);

        _root = TestSource.StartActivity("test-root")!;
    }

    public void Dispose()
    {
        AetherTracingRuntime.Configure(_originalLevel);
        _root.Dispose();
        foreach (var listener in _listeners)
        {
            listener.Dispose();
        }

        Activity.Current = null;
    }

    private ActivityListener CreateListener(List<Activity> collected)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BBT.Workflow.Cache",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            // Only this test's spans — see the TestSource remark.
            ActivityStopped = a => { if (a.TraceId == _root.TraceId) collected.Add(a); }
        };
        ActivitySource.AddActivityListener(listener);
        _listeners.Add(listener);
        return listener;
    }

    [Fact]
    public void StartActivity_InBusinessMode_CreatesExportableSpan()
    {
        // Arrange: DetailLevel = Business, listener attached
        var collected = new List<Activity>();
        using var listener = CreateListener(collected);

        // Act
        using (var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationGet, "wf:domain:key:res:gen1:latest", "workflow"))
        {
            Assert.NotNull(activity);
        }

        // Assert: span exists even though DetailLevel is Business, not Verbose, and its name
        // carries the cache key so a reader sees WHAT was read without opening the span.
        var span = Assert.Single(collected);
        Assert.Equal("Cache.Get/wf:domain:key:res:gen1:latest", span.DisplayName);
        Assert.False(span.DisplayName.StartsWith("["));
        Assert.Equal("wf:domain:key:res:gen1:latest", span.GetTagItem("cache.key"));
        Assert.Equal(TelemetryConstants.SpanCategories.Business,
            span.GetTagItem(TelemetryConstants.TagNames.SpanCategory));
    }

    [Fact]
    public void StartActivity_WithoutCacheKey_KeepsBareOperationName()
    {
        // A keyless operation (warmup, batch) must not degrade into a trailing-slash name.
        var collected = new List<Activity>();
        using var listener = CreateListener(collected);

        using (CacheActivityHelper.StartActivity(CacheActivityHelper.OperationWarmup))
        {
        }

        Assert.Equal("Cache.Warmup", Assert.Single(collected).DisplayName);
    }

    [Fact]
    public void SetL1Hit_True_RecordsL1HitTag()
    {
        // Arrange: an L1 hit must be visible as a tag, not suppressed (spec §4/§7 — L1 hits are
        // spans with a tag).
        var collected = new List<Activity>();
        using var listener = CreateListener(collected);

        using (var activity = CacheActivityHelper.StartActivity(CacheActivityHelper.OperationGet))
        {
            CacheActivityHelper.SetCacheHit(activity, true);
            CacheActivityHelper.SetL1Hit(activity, true);
        }

        var span = collected.Single();
        Assert.Equal(true, span.GetTagItem("cache.hit"));
        Assert.Equal(true, span.GetTagItem("cache.l1.hit"));
    }

    [Fact]
    public void SetL1Hit_False_RecordsL2HitWithinSpanDuration()
    {
        // When l1_hit=false, the span itself is expected to cover the L2 (Dapr) read — callers keep
        // the backend get call inside the same using-scope as StartActivity (see CacheSet.cs). This
        // test only pins the tag; the enclosing-duration contract is verified by CacheSet's own
        // structure, checked in Task 10's manual review.
        var collected = new List<Activity>();
        using var listener = CreateListener(collected);

        using (var activity = CacheActivityHelper.StartActivity(CacheActivityHelper.OperationGet))
        {
            CacheActivityHelper.SetCacheHit(activity, true);
            CacheActivityHelper.SetL1Hit(activity, false);
        }

        var span = collected.Single();
        Assert.Equal(false, span.GetTagItem("cache.l1.hit"));
    }
}
