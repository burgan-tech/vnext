using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Caching;

/// <summary>
/// Tracing contract of the cache-aside path in <see cref="CacheSet{T}"/>.
/// <para>
/// Two gaps this pins. First, the write-back after a miss went through a private helper that
/// emitted nothing, so the Redis write was folded anonymously into the parent <c>Cache.Get</c>
/// and a failed write — which is deliberately swallowed — left no trace at all. Second, a reader
/// had to infer which layer answered from a <c>cache.hit</c> + <c>cache.l1.hit</c> combination;
/// <c>cache.source</c> states it outright.
/// </para>
/// </summary>
public sealed class CacheSetSpanTests : IDisposable
{
    private const string Full = "1.0.0-pkg.1.0.0";

    /// <summary>
    /// Root source for the per-test ambient activity. Cache spans inherit its trace id, which is how
    /// <see cref="Named"/> tells this test's spans apart from every other test's.
    /// </summary>
    private static readonly ActivitySource TestSource = new("CacheSetSpanTests");

    private readonly List<Activity> _spans = new();
    private readonly ActivityListener _listener;
    private readonly Activity _root;

    public CacheSetSpanTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "BBT.Workflow.Cache" or "CacheSetSpanTests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _spans.Add
        };
        ActivitySource.AddActivityListener(_listener);

        // The listener is process-wide, so it also receives cache spans from tests running
        // concurrently in other collections. Anchoring each test to its own root activity and
        // filtering on the trace id is what keeps this suite honest — without it these assertions
        // pass alone and fail in a full run, which is worse than having no test at all.
        _root = TestSource.StartActivity("test-root")!;
    }

    public void Dispose()
    {
        _root.Dispose();
        _listener.Dispose();
    }

    private IEnumerable<Activity> Named(string operation) =>
        _spans.Where(a => a.TraceId == _root.TraceId
                          && (a.DisplayName == operation
                              || a.DisplayName.StartsWith(operation + "/", StringComparison.Ordinal)));

    private static string? Source(Activity a) =>
        a.GetTagItem("cache.source") as string;

    [Fact]
    public async Task Backend_resolution_emits_a_write_span_for_the_cache_write_back()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish(Full);

        var result = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);

        result.IsSuccess.ShouldBeTrue();
        Named("Cache.Write").ShouldNotBeEmpty(
            "the write-back after a backend load is a distributed write and must be attributable");
    }

    [Fact]
    public async Task A_read_served_from_the_backend_reports_its_source_as_backend()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish(Full);

        await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);

        Named("Cache.Get").Select(Source).ShouldContain("backend");
    }

    [Fact]
    public async Task A_read_served_from_L1_reports_its_source_as_l1()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish(Full);

        await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);
        _spans.Clear();
        await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);

        Named("Cache.Get").Select(Source).ShouldAllBe(s => s == "l1");
    }

    [Fact]
    public async Task A_read_served_from_the_distributed_store_reports_its_source_as_l2()
    {
        // L1 off, so the second read is answered by the distributed store rather than in-process —
        // the only way to isolate the l2 source without reaching into private key construction.
        var harness = new CacheSetTestHarness(o => o.L1Enabled = false);
        harness.Publish(Full);

        await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);
        _spans.Clear();
        await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);

        Named("Cache.Get").Select(Source).ShouldAllBe(s => s == "l2");
    }

    [Fact]
    public async Task A_failed_distributed_write_is_an_error_span_and_still_does_not_throw()
    {
        var harness = new CacheSetTestHarness();
        harness.Cache.FailWrites = _ => true;
        harness.Publish(Full);

        var result = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);

        // The swallow is deliberate — a cache that cannot be written is still a correct read.
        // What was missing is any sign of it outside the log.
        result.IsSuccess.ShouldBeTrue("a failed cache write must not fail the read");
        Named("Cache.Write").ShouldContain(a => a.Status == ActivityStatusCode.Error);
    }
}
