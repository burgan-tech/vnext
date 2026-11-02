using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.DistributedCache;
using BBT.Aether.DistributedLock;
using BBT.Workflow.Files;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Files;

/// <summary>
/// A staged upload is taken (read and removed) under a lock keyed by the file, so two requests claiming the same
/// streamed upload at once cannot both get it; a request that fails afterwards puts it back until it expires.
/// </summary>
public sealed class DistributedFileStagingStoreTests
{
    private readonly IDistributedCacheService _cache = Substitute.For<IDistributedCacheService>();
    private readonly IDistributedLockService _locks = Substitute.For<IDistributedLockService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly DistributedFileStagingStore _sut;
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    public DistributedFileStagingStoreTests()
    {
        _clock.UtcNow.Returns(Now);
        _sut = new DistributedFileStagingStore(_cache, _locks, _clock);
    }

    private static StagedFile Staged(DateTimeOffset expiresAt) => new()
    {
        Domain = "core", Flow = "kyc", Path = "files[]", Component = "store-a", File = "f1", Size = 1, ETag = "e", ExpiresAt = expiresAt,
    };

    private void LockIsFree()
        => _locks.ExecuteWithLockAsync(Arg.Any<string>(), Arg.Any<Func<Task<StagedFile?>>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async ci => (true, await ci.ArgAt<Func<Task<StagedFile?>>>(1)()));

    [Fact]
    public async Task Take_ReadsAndRemovesTheEntry_UnderALockKeyedByTheFile()
    {
        LockIsFree();
        var staged = Staged(Now.AddHours(1));
        _cache.GetAsync<StagedFile>("vnext:core:file-upload:f1", Arg.Any<CancellationToken>()).Returns(staged);

        (await _sut.TakeAsync("core", "f1", default)).ShouldBeSameAs(staged);

        await _cache.Received(1).RemoveAsync("vnext:core:file-upload:f1", Arg.Any<CancellationToken>());
        await _locks.Received(1).ExecuteWithLockAsync("vnext:core:file-upload:f1:claim",
            Arg.Any<Func<Task<StagedFile?>>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Take_OfAMissingEntry_RemovesNothing()
    {
        LockIsFree();
        (await _sut.TakeAsync("core", "f1", default)).ShouldBeNull();
        await _cache.DidNotReceiveWithAnyArgs().RemoveAsync(default!, default);
    }

    [Fact]
    public async Task Take_WhileAnotherRequestHoldsTheLock_GetsNothing_AndDoesNotWait()
    {
        _locks.ExecuteWithLockAsync(Arg.Any<string>(), Arg.Any<Func<Task<StagedFile?>>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((false, (StagedFile?)null));

        (await _sut.TakeAsync("core", "f1", default)).ShouldBeNull();
        await _cache.DidNotReceiveWithAnyArgs().GetAsync<StagedFile>(default!, default);
    }

    [Fact]
    public async Task Restore_PutsTheTicketBack_UntilItsOwnExpiry()
    {
        var staged = Staged(Now.AddMinutes(20));
        await _sut.RestoreAsync(staged, default);
        await _cache.Received(1).SetAsync("vnext:core:file-upload:f1", staged, Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Restore_OfAnExpiredTicket_DoesNothing()
    {
        await _sut.RestoreAsync(Staged(Now.AddSeconds(-1)), default);
        await _cache.DidNotReceiveWithAnyArgs().SetAsync<StagedFile>(default!, default!, default!, default);
    }
}
