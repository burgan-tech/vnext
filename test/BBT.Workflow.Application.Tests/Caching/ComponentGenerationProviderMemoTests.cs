using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Caching;

/// <summary>
/// Pins the in-process generation-token memoization semantics (<c>GenerationMemoSeconds</c>, Phase 2).
/// The contract these tests protect: a memo hit spends no distributed read; the window expires on the
/// injected clock; a bump NEVER leaves a pre-bump token memoized (the publishing pod is immediately
/// fresh, even when the bump write fails); and the default (0) keeps one read per resolution.
/// </summary>
public class ComponentGenerationProviderMemoTests
{
    private const string GenKey = "sys-tasks:core:k:gen";

    private sealed class FixedGuids : IGuidGenerator
    {
        private int _n;
        public Guid Create() => new(Interlocked.Increment(ref _n), 0, 0, new byte[8]);
    }

    private static (ComponentGenerationProvider Sut,
                    FakeDistributedCacheService Cache,
                    CacheSetTestHarness.AdjustableTimeProvider Time)
        Create(int memoSeconds)
    {
        var time = new CacheSetTestHarness.AdjustableTimeProvider(
            new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
        var cache = new FakeDistributedCacheService(time);
        var sut = new ComponentGenerationProvider(
            cache,
            new FixedGuids(),
            Microsoft.Extensions.Options.Options.Create(
                new ComponentCacheOptions { GenerationMemoSeconds = memoSeconds }),
            time,
            NullLogger<ComponentGenerationProvider>.Instance);
        return (sut, cache, time);
    }

    [Fact]
    public async Task Memo_hit_skips_the_distributed_read()
    {
        var (sut, cache, _) = Create(5);
        var first = await sut.GetAsync("sys-tasks", "core", "k");
        cache.ClearLog();

        var second = await sut.GetAsync("sys-tasks", "core", "k");

        second.ShouldBe(first);
        cache.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Memo_expires_after_the_window_and_rereads()
    {
        var (sut, cache, time) = Create(5);
        await sut.GetAsync("sys-tasks", "core", "k");
        cache.ClearLog();

        time.Advance(TimeSpan.FromSeconds(6));
        await sut.GetAsync("sys-tasks", "core", "k");

        cache.Reads.ShouldContain(GenKey);
    }

    [Fact]
    public async Task Bump_replaces_the_memo_so_the_publishing_pod_is_immediately_fresh()
    {
        var (sut, cache, _) = Create(5);
        var before = await sut.GetAsync("sys-tasks", "core", "k");

        var bumped = await sut.BumpAsync("sys-tasks", "core", "k");
        cache.ClearLog();
        var after = await sut.GetAsync("sys-tasks", "core", "k");

        after.ShouldBe(bumped);
        after.ShouldNotBe(before);
        cache.Reads.ShouldBeEmpty("bump memoizes the new token; no re-read needed");
    }

    [Fact]
    public async Task Failed_bump_write_leaves_no_memo_behind()
    {
        var (sut, cache, _) = Create(5);
        await sut.GetAsync("sys-tasks", "core", "k");

        cache.FailWrites = key => key == GenKey;
        cache.FailRemoves = key => key == GenKey;
        await sut.BumpAsync("sys-tasks", "core", "k");

        cache.FailWrites = null;
        cache.FailRemoves = null;
        cache.ClearLog();
        await sut.GetAsync("sys-tasks", "core", "k");

        cache.Reads.ShouldContain(GenKey, "memo must be dropped even when the bump write fails");
    }

    [Fact]
    public async Task Disabled_memo_reads_the_distributed_cache_every_time()
    {
        var (sut, cache, _) = Create(0);
        await sut.GetAsync("sys-tasks", "core", "k");
        cache.ClearLog();

        await sut.GetAsync("sys-tasks", "core", "k");

        cache.Reads.ShouldContain(GenKey);
    }
}
