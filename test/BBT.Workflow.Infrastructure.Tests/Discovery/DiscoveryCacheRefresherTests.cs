using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedLock;
using BBT.Aether.Results;
using BBT.Workflow.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Discovery;

/// <summary>
/// Pins the refresh guard on <see cref="DiscoveryCacheRefresher"/>: exactly one bulk read per window
/// across the cluster, no stampede when the lock is released, and no permanent stall when a replica
/// dies holding it.
/// </summary>
/// <remarks>
/// The guard is two-part on purpose and these tests separate the halves.
/// <c>DomainDiscoveryInitializationHostedService</c> gets away with never releasing its lease because
/// for a once-per-rollout job the lease IS the guard. A periodic refresh must be able to re-acquire,
/// so the window is defined by a shared marker and the lease is always released — which only works if
/// the marker is checked both before and inside the lock.
/// </remarks>
public sealed class DiscoveryCacheRefresherTests
{
    [Fact]
    public async Task Marker_present_skips_the_lock_entirely()
    {
        var harness = new Harness { Marker = "2026-01-01T00:00:00Z" };

        var outcome = await harness.CreateSut().RefreshAsync(force: false, CancellationToken.None);

        // The anti-stampede step: the ordinary attempt on every replica but one must not even contend
        // for the lock, or a released lease turns into N bulk reads in a row.
        outcome.ShouldBe(DiscoveryCacheRefreshOutcome.SkippedWindowFresh);
        await harness.LockService.DidNotReceive()
            .TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        harness.ListAllCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Lock_not_acquired_skips_the_bulk_read()
    {
        var harness = new Harness { LockAcquirable = false };

        var outcome = await harness.CreateSut().RefreshAsync(force: false, CancellationToken.None);

        outcome.ShouldBe(DiscoveryCacheRefreshOutcome.SkippedNotOwner);
        harness.ListAllCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Marker_written_between_the_first_check_and_the_lock_still_prevents_a_second_read()
    {
        var harness = new Harness();
        harness.OnLockAcquired = () => harness.Marker = "2026-01-01T00:00:00Z";

        var outcome = await harness.CreateSut().RefreshAsync(force: false, CancellationToken.None);

        // The double-check inside the lock. Without it, every replica queued on the lock does a full
        // bulk read the moment the winner releases.
        outcome.ShouldBe(DiscoveryCacheRefreshOutcome.SkippedWindowFresh);
        harness.ListAllCalls.ShouldBe(0);
        harness.Handle.Released.ShouldBeTrue();
    }

    [Fact]
    public async Task A_successful_refresh_publishes_the_registrations_and_claims_the_window()
    {
        var harness = new Harness();

        var outcome = await harness.CreateSut().RefreshAsync(force: false, CancellationToken.None);

        outcome.ShouldBe(DiscoveryCacheRefreshOutcome.Refreshed);
        harness.ListAllCalls.ShouldBe(1);
        harness.Published!.Count.ShouldBe(2);
        harness.Marker.ShouldNotBeNull();
        harness.Handle.Released.ShouldBeTrue();
    }

    [Fact]
    public async Task The_lease_is_released_even_when_the_bulk_read_throws()
    {
        var harness = new Harness { ListAllThrows = true };

        var outcome = await harness.CreateSut().RefreshAsync(force: false, CancellationToken.None);

        // A leaked lease would stall every subsequent window until it expired, for no reason.
        outcome.ShouldBe(DiscoveryCacheRefreshOutcome.Failed);
        harness.Handle.Released.ShouldBeTrue();
        harness.Marker.ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_bulk_read_leaves_the_previous_entries_alone_and_does_not_claim_the_window()
    {
        var harness = new Harness { ListAllSucceeds = false };

        var outcome = await harness.CreateSut().RefreshAsync(force: false, CancellationToken.None);

        // Publishing a failure would evict good entries in favour of nothing; claiming the window
        // would then hide the failure until the next one.
        outcome.ShouldBe(DiscoveryCacheRefreshOutcome.Failed);
        harness.Published.ShouldBeNull();
        harness.Marker.ShouldBeNull();
    }

    [Fact]
    public async Task An_empty_registry_answer_is_treated_as_a_failed_window()
    {
        var harness = new Harness { Registrations = [] };

        var outcome = await harness.CreateSut().RefreshAsync(force: false, CancellationToken.None);

        // If the registry ever applies caller-role filtering to its instance list, an
        // under-authenticated refresher gets 200 with zero items — and publishing that would record
        // "the cluster has no domains" with complete confidence.
        outcome.ShouldBe(DiscoveryCacheRefreshOutcome.Failed);
        harness.Published.ShouldBeNull();
        harness.Marker.ShouldBeNull();
    }

    [Fact]
    public async Task A_refresh_failure_never_throws()
    {
        var harness = new Harness { LockThrows = true };

        // Unlike the registration hosted service, which rethrows to abort startup, an unwarmed cache
        // is not a broken pod: every lookup simply falls back to the live registry.
        var sut = harness.CreateSut();
        DiscoveryCacheRefreshOutcome outcome = default;

        await Should.NotThrowAsync(async () =>
            outcome = await sut.RefreshAsync(force: false, CancellationToken.None));

        outcome.ShouldBe(DiscoveryCacheRefreshOutcome.Failed);
    }

    [Fact]
    public async Task A_dead_holder_does_not_stall_the_next_window()
    {
        var harness = new Harness { LockAcquirable = false };
        var sut = harness.CreateSut();

        await sut.RefreshAsync(force: false, CancellationToken.None);
        harness.ListAllCalls.ShouldBe(0);

        // The lease expired and no marker was written, so the very next attempt simply proceeds. The
        // stall is bounded by lease + tick interval, never permanent.
        harness.LockAcquirable = true;
        await sut.RefreshAsync(force: false, CancellationToken.None);

        harness.ListAllCalls.ShouldBe(1);
        harness.Marker.ShouldNotBeNull();
    }

    // ────────────────────────────────────────────────────────────────────
    // Forced refresh — the operator escape hatch
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_forced_refresh_reads_the_registry_even_inside_a_served_window()
    {
        var harness = new Harness { Marker = "2026-01-01T00:00:00Z" };

        var outcome = await harness.CreateSut().RefreshAsync(force: true, CancellationToken.None);

        // The whole point of the escape hatch. With an hour-long window, an operator responding to a
        // misrouting incident cannot be told "already refreshed this hour" — that is exactly the
        // situation they are trying to correct.
        outcome.ShouldBe(DiscoveryCacheRefreshOutcome.Refreshed);
        harness.ListAllCalls.ShouldBe(1);
        harness.Published!.Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_forced_refresh_still_takes_the_lock()
    {
        var harness = new Harness { Marker = "2026-01-01T00:00:00Z", LockAcquirable = false };

        var outcome = await harness.CreateSut().RefreshAsync(force: true, CancellationToken.None);

        // Bypassing the window must not also bypass the mutex: two operators clicking at once, or a
        // forced refresh racing the timer, must not both read the registry.
        outcome.ShouldBe(DiscoveryCacheRefreshOutcome.SkippedNotOwner);
        harness.ListAllCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_forced_refresh_restarts_the_window()
    {
        var harness = new Harness { Marker = "stale" };
        var sut = harness.CreateSut();

        await sut.RefreshAsync(force: true, CancellationToken.None);
        harness.Marker.ShouldBe("claimed");

        // And the next scheduled attempt sees a fresh window rather than immediately re-reading.
        await sut.RefreshAsync(force: false, CancellationToken.None);
        harness.ListAllCalls.ShouldBe(1);
    }

    // ────────────────────────────────────────────────────────────────────
    // Harness
    // ────────────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public string? Marker { get; set; }
        public bool LockAcquirable { get; set; } = true;
        public bool LockThrows { get; set; }
        public bool ListAllSucceeds { get; set; } = true;
        public bool ListAllThrows { get; set; }
        public int ListAllCalls { get; private set; }
        public IReadOnlyList<DomainRegistration>? Published { get; private set; }
        public Action? OnLockAcquired { get; set; }

        public IReadOnlyList<DomainRegistration> Registrations { get; set; } =
        [
            new("core", "https://core.test", "vnext-core-app", null),
            new("sales", "https://sales.test", "vnext-sales-app", null)
        ];

        public IDistributedLockService LockService { get; } = Substitute.For<IDistributedLockService>();
        public FakeHandle Handle { get; } = new();

        public DiscoveryCacheRefresher CreateSut()
        {
            LockService.TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    if (LockThrows)
                        throw new InvalidOperationException("lock store unavailable");

                    if (!LockAcquirable)
                        return Task.FromResult<IDistributedLockHandle?>(null);

                    OnLockAcquired?.Invoke();
                    return Task.FromResult<IDistributedLockHandle?>(Handle);
                });

            var registryClient = Substitute.For<IDiscoveryRegistryClient>();
            registryClient.ListAllAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            {
                ListAllCalls++;

                if (ListAllThrows)
                    throw new InvalidOperationException("registry unavailable");

                return ListAllSucceeds
                    ? Task.FromResult(Result<IReadOnlyList<DomainRegistration>>.Ok(Registrations))
                    : Task.FromResult(Result<IReadOnlyList<DomainRegistration>>.Fail(
                        new Error("Discovery", "Discovery:700002", "registry down")));
            });

            var cacheWriter = Substitute.For<IDiscoveryCacheWriter>();
            cacheWriter.GetRefreshMarkerAsync(Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(Marker));
            cacheWriter.SetRefreshMarkerAsync(Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    Marker = "claimed";
                    return Task.CompletedTask;
                });
            cacheWriter
                .When(w => w.SetAsync(Arg.Any<IReadOnlyList<DomainRegistration>>(), Arg.Any<CancellationToken>()))
                .Do(call => Published = call.Arg<IReadOnlyList<DomainRegistration>>());

            return new DiscoveryCacheRefresher(
                registryClient,
                cacheWriter,
                LockService,
                Options.Create(new ServiceDiscoveryOptions
                {
                    Enabled = true,
                    BaseUrl = "https://discovery.test",
                    Domain = "discovery",
                    Cache = new DiscoveryCacheOptions { Enabled = true }
                }),
                NullLogger<DiscoveryCacheRefresher>.Instance);
        }
    }

    private sealed class FakeHandle : IDistributedLockHandle
    {
        public bool Released { get; private set; }
        public string LockKey => DiscoveryCacheRefresher.RefreshLockKey;
        public string Owner => "test";

        public Task<bool> ExtendAsync(int leaseSeconds, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseAsync(CancellationToken cancellationToken = default)
        {
            Released = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Released = true;
            return ValueTask.CompletedTask;
        }
    }
}
