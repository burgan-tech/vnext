using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Caching;

/// <summary>
/// Pins that caller cancellation PROPAGATES out of the generation provider instead of being folded
/// into its infrastructure-failure degradation.
/// <para>
/// The provider deliberately fails open on real cache failures: it fabricates an unshared token so
/// the caller resolves from the backend instead of erroring (see <see cref="ComponentGenerationProvider"/>'s
/// own remarks). Cancellation must NOT take that path — an abandoned request would be handed a
/// fabricated token and would then keep doing work (resolving components, compiling scripts) that
/// nobody is waiting for, and a cancelled bump would report success for a write that never landed.
/// </para>
/// </summary>
public class ComponentGenerationProviderCancellationTests
{
    private sealed class FixedGuids : IGuidGenerator
    {
        private int _n;
        public Guid Create() => new(Interlocked.Increment(ref _n), 0, 0, new byte[8]);
    }

    private static (ComponentGenerationProvider Sut, FakeDistributedCacheService Cache) Create()
    {
        var time = new CacheSetTestHarness.AdjustableTimeProvider(
            new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));
        var cache = new FakeDistributedCacheService(time);
        var sut = new ComponentGenerationProvider(
            cache,
            new FixedGuids(),
            Microsoft.Extensions.Options.Options.Create(new ComponentCacheOptions()),
            time,
            NullLogger<ComponentGenerationProvider>.Instance);
        return (sut, cache);
    }

    [Fact]
    public async Task GetAsync_WhenCallerCancelled_PropagatesInsteadOfFabricatingAToken()
    {
        var (sut, _) = Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => sut.GetAsync("sys-mappings", "core", "helper", cts.Token));
    }

    [Fact]
    public async Task BumpAsync_WhenCallerCancelled_PropagatesInsteadOfReportingSuccess()
    {
        var (sut, _) = Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            () => sut.BumpAsync("sys-mappings", "core", "helper", cts.Token));
    }

    [Fact]
    public async Task GetAsync_WhenCacheReadFails_StillFailsOpenWithAFabricatedToken()
    {
        // The degradation contract for REAL failures must survive the cancellation fix.
        var (sut, cache) = Create();
        cache.FailReads = _ => true;

        var token = await sut.GetAsync("sys-mappings", "core", "helper");

        token.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task BumpAsync_WhenWriteFails_StillFallsBackToRemoveAndReturnsAToken()
    {
        var (sut, cache) = Create();
        cache.FailWrites = _ => true;

        var token = await sut.BumpAsync("sys-mappings", "core", "helper");

        token.ShouldNotBeNullOrEmpty();
        cache.Removes.ShouldContain("sys-mappings:core:helper:gen");
    }
}
