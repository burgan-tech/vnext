using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Functions.Caching;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Functions;

/// <summary>Unit tests for <see cref="FunctionCacheLocalTier"/> (per-pod L1 copy and single-flight).</summary>
public sealed class FunctionCacheLocalTierTests
{
    private static FunctionCacheLocalTier Create(int localTtlMs = 0, bool singleFlight = true) =>
        new(Options.Create(new FunctionCacheOptions { LocalTtlMilliseconds = localTtlMs, SingleFlight = singleFlight }));

    private static FunctionResponseOutput Response(int v) =>
        new() { Data = new Dictionary<string, object?> { ["v"] = v }, StatusCode = 200 };

    [Fact]
    public void Disabled_HoldsNothing()
    {
        var tier = Create();

        tier.SetResponse("s", "k", Response(1));
        tier.SetGeneration("s", "g", "7");

        tier.LocalEnabled.ShouldBeFalse();
        tier.TryGetResponse("s", "k").ShouldBeNull();
        tier.TryGetGeneration("s", "g", out _).ShouldBeFalse();
    }

    [Fact]
    public void Enabled_ReturnsACopyPerRead_AndKeysByStore()
    {
        var tier = Create(localTtlMs: 60_000);
        tier.SetResponse("s", "k", Response(1));
        tier.SetGeneration("s", "g", "7");

        var a = tier.TryGetResponse("s", "k");
        var b = tier.TryGetResponse("s", "k");

        a.ShouldNotBeNull();
        b.ShouldNotBeNull();
        ReferenceEquals(a, b).ShouldBeFalse();
        a.StatusCode.ShouldBe(200);
        tier.TryGetResponse("other-store", "k").ShouldBeNull();
        tier.TryGetGeneration("s", "g", out var generation).ShouldBeTrue();
        generation.ShouldBe("7");
    }

    [Fact]
    public async Task Enabled_EntriesExpireAfterTheLocalTtl()
    {
        var tier = Create(localTtlMs: 50);
        tier.SetResponse("s", "k", Response(1));

        await Task.Delay(200);

        tier.TryGetResponse("s", "k").ShouldBeNull();
    }

    [Fact]
    public async Task RunOnce_FollowerSharesLeadersSuccess()
    {
        var tier = Create();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;

        async Task<Result<FunctionResponseOutput>> Execute()
        {
            Interlocked.Increment(ref executions);
            await release.Task;
            return Result<FunctionResponseOutput>.Ok(Response(1));
        }

        var leader = tier.RunOnceAsync("k", Execute, CancellationToken.None);
        var follower = tier.RunOnceAsync("k", Execute, CancellationToken.None);
        release.SetResult();

        (await leader).IsSuccess.ShouldBeTrue();
        (await follower).Value!.StatusCode.ShouldBe(200);
        executions.ShouldBe(1);
    }

    [Fact]
    public async Task RunOnce_FollowerRunsItselfWhenLeaderFails()
    {
        var tier = Create();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;

        async Task<Result<FunctionResponseOutput>> Execute()
        {
            if (Interlocked.Increment(ref executions) == 1)
            {
                await release.Task;
                return Result<FunctionResponseOutput>.Fail(Error.Failure("boom", "leader failed"));
            }
            return Result<FunctionResponseOutput>.Ok(Response(2));
        }

        var leader = tier.RunOnceAsync("k", Execute, CancellationToken.None);
        var follower = tier.RunOnceAsync("k", Execute, CancellationToken.None);
        release.SetResult();

        (await leader).IsSuccess.ShouldBeFalse();
        (await follower).IsSuccess.ShouldBeTrue();
        executions.ShouldBe(2);
    }

    [Fact]
    public async Task RunOnce_FollowerRunsItselfWhenLeaderThrows()
    {
        var tier = Create();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;

        async Task<Result<FunctionResponseOutput>> Execute()
        {
            if (Interlocked.Increment(ref executions) == 1)
            {
                await release.Task;
                throw new InvalidOperationException("leader threw");
            }
            return Result<FunctionResponseOutput>.Ok(Response(3));
        }

        var leader = tier.RunOnceAsync("k", Execute, CancellationToken.None);
        var follower = tier.RunOnceAsync("k", Execute, CancellationToken.None);
        release.SetResult();

        await Should.ThrowAsync<InvalidOperationException>(leader);
        (await follower).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task RunOnce_DisabledSingleFlight_RunsEveryCaller()
    {
        var tier = Create(singleFlight: false);
        var executions = 0;

        Task<Result<FunctionResponseOutput>> Execute()
        {
            Interlocked.Increment(ref executions);
            return Task.FromResult(Result<FunctionResponseOutput>.Ok(Response(1)));
        }

        await tier.RunOnceAsync("k", Execute, CancellationToken.None);
        await tier.RunOnceAsync("k", Execute, CancellationToken.None);

        executions.ShouldBe(2);
    }

    [Fact]
    public async Task RunOnce_DifferentKeysDoNotCoalesce()
    {
        var tier = Create();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;

        async Task<Result<FunctionResponseOutput>> Execute()
        {
            Interlocked.Increment(ref executions);
            await release.Task;
            return Result<FunctionResponseOutput>.Ok(Response(1));
        }

        var a = tier.RunOnceAsync("a", Execute, CancellationToken.None);
        var b = tier.RunOnceAsync("b", Execute, CancellationToken.None);
        release.SetResult();
        await Task.WhenAll(a, b);

        executions.ShouldBe(2);
    }
}
