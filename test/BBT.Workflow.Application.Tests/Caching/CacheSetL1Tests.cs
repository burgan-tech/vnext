using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Caching;

/// <summary>
/// L1 behavior of <see cref="CacheSet{T}"/>: hits skip the distributed store, publish freshness is
/// carried by the generation-scoped keys, negatives bypass L1, and disabling the layer reproduces
/// the previous read traffic exactly.
/// </summary>
public class CacheSetL1Tests
{
    private const string Full = "1.0.0-pkg.1.0.0";

    [Fact]
    public async Task Second_full_version_read_is_served_from_L1_without_an_L2_read()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish(Full);

        var first = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);
        var second = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        harness.Cache.Reads.Count(k => k == harness.FullKey(Full)).ShouldBe(1);
        harness.Backend.LoadCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Second_latest_read_costs_only_the_generation_read()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish(Full);

        await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, "latest");
        harness.Cache.ClearLog();

        var second = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, "latest");

        second.IsSuccess.ShouldBeTrue();
        harness.Cache.Reads.ShouldBe([harness.GenerationKey()]);
    }

    [Fact]
    public async Task A_publish_is_visible_immediately_despite_a_warm_L1()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish("1.0.0-pkg.1.0.0");
        await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, "latest");

        const string newer = "1.1.0-pkg.1.1.0";
        harness.AddPublished(newer);
        await harness.Sut.SetAsync(CacheSetTestHarness.CreateView(newer));

        var resolved = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, "latest");

        resolved.IsSuccess.ShouldBeTrue();
        resolved.Value!.Version.ShouldBe(newer);
    }

    [Fact]
    public async Task Invalidate_removes_the_full_body_from_L1_too()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish(Full);
        await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);

        harness.Deactivate(Full);
        await harness.Sut.InvalidateAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);

        var after = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);

        after.IsSuccess.ShouldBeFalse();
        harness.Backend.LoadCallCount.ShouldBe(2);
    }

    [Fact]
    public async Task L1_hits_return_distinct_instances()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish(Full);

        var first = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);
        var second = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);

        ReferenceEquals(first.Value, second.Value).ShouldBeFalse();
    }

    [Fact]
    public async Task Negative_answers_are_served_from_L2_not_L1()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish(Full);

        var miss1 = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, "9");
        var generation = await harness.CurrentGenerationAsync();
        var miss2 = await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, "9");

        miss1.IsSuccess.ShouldBeFalse();
        miss2.IsSuccess.ShouldBeFalse();
        harness.Cache.Reads.Count(k => k == harness.ResolutionKey(generation, "9")).ShouldBe(2);
    }

    [Fact]
    public async Task Disabled_L1_reproduces_the_previous_read_counts()
    {
        var harness = new CacheSetTestHarness(o => o.L1Enabled = false);
        harness.Publish(Full);

        await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);
        await harness.Sut.GetByVersionAsync(
            CacheSetTestHarness.TestDomain, CacheSetTestHarness.TestKey, Full);

        harness.Cache.Reads.Count(k => k == harness.FullKey(Full)).ShouldBe(2);
    }
}
