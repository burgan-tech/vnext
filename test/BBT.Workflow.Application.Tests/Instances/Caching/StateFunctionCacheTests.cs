using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedCache;
using BBT.Aether.Users;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// Unit tests for <see cref="StateFunctionCache"/>: cache-key composition (caller scoping),
/// TTL propagation, failure-degrades-to-miss behavior, and envelope JSON round-trip.
/// </summary>
public class StateFunctionCacheTests
{
    private const string TestDomain = "test-domain";
    private const string TestWorkflow = "test-flow";
    private const string TestInstance = "instance-1";

    private readonly IDistributedCacheService _distributedCache = Substitute.For<IDistributedCacheService>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    private StateFunctionCache CreateSut(StateFunctionCacheOptions? options = null) =>
        new(_distributedCache,
            _currentUser,
            Options.Create(options ?? new StateFunctionCacheOptions()),
            Substitute.For<ILogger<StateFunctionCache>>());

    private static GetInstanceStateInput CreateInput(
        string? role = null,
        IReadOnlyList<string>? roles = null,
        string[]? extensions = null,
        string? version = null,
        Dictionary<string, string?>? headers = null) => new()
    {
        Domain = TestDomain,
        Workflow = TestWorkflow,
        Instance = TestInstance,
        Role = role,
        Roles = roles,
        Extensions = extensions,
        Version = version,
        Headers = headers ?? new Dictionary<string, string?>(),
        QueryParams = new Dictionary<string, string?>()
    };

    [Fact]
    public void Options_Defaults_AreEnabledWith60SecondTtl()
    {
        var options = new StateFunctionCacheOptions();

        options.Enabled.ShouldBeTrue();
        options.TtlSeconds.ShouldBe(60);
    }

    [Fact]
    public void BuildKey_ContainsResponseShapeVersionDomainWorkflowAndInstance()
    {
        var key = CreateSut().BuildKey(CreateInput());

        // The response-shape version segment is part of the key so that a runtime change to the
        // state body (v2 added the workflow-level updateData/exit transitions; v3 the workflow's
        // `functions` discovery links; v4 replaced that list with a hasFunctions flag plus a catalog
        // link) cannot be served from entries written by an earlier build.
        // Bump this literal in the same commit as ResponseShapeVersion — the assertion exists to make
        // a silent shape change impossible.
        key.ShouldStartWith($"state-fn:v5:{TestDomain}:{TestWorkflow}:{TestInstance}:");
    }

    [Fact]
    public void BuildKey_IsRolesOrderInsensitive()
    {
        var sut = CreateSut();

        var key1 = sut.BuildKey(CreateInput(roles: ["backoffice", "admin"]));
        var key2 = sut.BuildKey(CreateInput(roles: ["admin", "backoffice"]));

        key1.ShouldBe(key2);
    }

    [Fact]
    public void BuildKey_DiffersByRoles()
    {
        var sut = CreateSut();

        var key1 = sut.BuildKey(CreateInput(roles: ["admin"]));
        var key2 = sut.BuildKey(CreateInput(roles: ["backoffice"]));

        key1.ShouldNotBe(key2);
    }

    [Fact]
    public void BuildKey_DiffersByActorIdentity()
    {
        var sut = CreateSut();

        _currentUser.ActorUserName.Returns("alice");
        var key1 = sut.BuildKey(CreateInput());

        _currentUser.ActorUserName.Returns("bob");
        var key2 = sut.BuildKey(CreateInput());

        key1.ShouldNotBe(key2);
    }

    [Fact]
    public void BuildKey_DiffersByCulture()
    {
        var sut = CreateSut();

        var key1 = sut.BuildKey(CreateInput(headers: new Dictionary<string, string?>
        {
            ["Accept-Language"] = "tr-TR"
        }));
        var key2 = sut.BuildKey(CreateInput(headers: new Dictionary<string, string?>
        {
            ["Accept-Language"] = "en-US"
        }));

        key1.ShouldNotBe(key2);
    }

    [Fact]
    public void BuildKey_DiffersByExtensionsAndVersion()
    {
        var sut = CreateSut();

        var baseline = sut.BuildKey(CreateInput());
        var withExtensions = sut.BuildKey(CreateInput(extensions: ["ext-a"]));
        var withVersion = sut.BuildKey(CreateInput(version: "2.0.0"));

        withExtensions.ShouldNotBe(baseline);
        withVersion.ShouldNotBe(baseline);
    }

    [Fact]
    public async Task GetAsync_WhenCacheThrows_ReturnsNull()
    {
        _distributedCache
            .GetAsync<StateFunctionCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var entry = await CreateSut().GetAsync("some-key", CancellationToken.None);

        entry.ShouldBeNull();
    }

    [Fact]
    public async Task SetAsync_WhenCacheThrows_DoesNotThrow()
    {
        _distributedCache
            .SetAsync(Arg.Any<string>(), Arg.Any<StateFunctionCacheEntry>(),
                Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis down"));

        await Should.NotThrowAsync(() =>
            CreateSut().SetAsync("some-key", new StateFunctionCacheEntry(), CancellationToken.None));
    }

    [Fact]
    public async Task SetAsync_UsesConfiguredTtl()
    {
        DistributedCacheEntryOptions? captured = null;
        _distributedCache
            .SetAsync(Arg.Any<string>(), Arg.Any<StateFunctionCacheEntry>(),
                Arg.Do<DistributedCacheEntryOptions>(o => captured = o), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var sut = CreateSut(new StateFunctionCacheOptions { TtlSeconds = 120 });
        var before = DateTimeOffset.UtcNow;
        await sut.SetAsync("some-key", new StateFunctionCacheEntry(), CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        captured.ShouldNotBeNull();
        captured!.AbsoluteExpiration.ShouldNotBeNull();
        captured.AbsoluteExpiration!.Value.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(119));
        captured.AbsoluteExpiration!.Value.ShouldBeLessThanOrEqualTo(after.AddSeconds(121));
    }

    [Fact]
    public void CacheEntry_JsonRoundTrip_PreservesEtagsAndOutput()
    {
        var entry = new StateFunctionCacheEntry
        {
            Etag = "etag-1",
            EntityEtag = "entity-1",
            Output = new GetInstanceStateOutput
            {
                State = "review",
                StateType = "intermediate",
                Status = InstanceStatus.Active,
                Transitions = [],
                ActiveCorrelations = []
            }
        };

        var json = JsonSerializer.Serialize(entry);
        var roundTripped = JsonSerializer.Deserialize<StateFunctionCacheEntry>(json);

        roundTripped.ShouldNotBeNull();
        roundTripped!.Etag.ShouldBe("etag-1");
        roundTripped.EntityEtag.ShouldBe("entity-1");
        roundTripped.Output.State.ShouldBe("review");
        roundTripped.Output.Status.ShouldBe(InstanceStatus.Active);
    }

    [Fact]
    public void ComputeEtag_IsDeterministic()
    {
        var sut = CreateSut();
        var fingerprint = CreateFingerprint();

        var etag1 = sut.ComputeEtag(CreateInput(roles: ["b", "a"]), fingerprint);
        var etag2 = sut.ComputeEtag(CreateInput(roles: ["a", "b"]), fingerprint);

        etag1.ShouldBe(etag2);
        etag1.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void ComputeEtag_ChangesWithEveryFingerprintComponent()
    {
        var sut = CreateSut();
        var input = CreateInput();
        var fingerprint = CreateFingerprint();
        var baseline = sut.ComputeEtag(input, fingerprint);

        sut.ComputeEtag(input, fingerprint with { Id = Guid.NewGuid() }).ShouldNotBe(baseline);
        sut.ComputeEtag(input, fingerprint with { EffectiveState = "approved" }).ShouldNotBe(baseline);
        sut.ComputeEtag(input, fingerprint with { Status = InstanceStatus.Busy }).ShouldNotBe(baseline);
        sut.ComputeEtag(input, fingerprint with { FlowVersion = "2.0.0" }).ShouldNotBe(baseline);
    }

    /// <summary>
    /// The response body carries the full correlation set, so every way that set can change must move the
    /// ETag even though the instance's own state, status and flow version are untouched. Without this a
    /// long-polling client would keep receiving 304 and never see a sub item start, finish or advance.
    /// </summary>
    [Fact]
    public void ComputeEtag_ChangesWithEveryCorrelationSetMutation()
    {
        var sut = CreateSut();
        var input = CreateInput();
        var fingerprint = CreateFingerprint(
            correlationCount: 2,
            completedCorrelationCount: 1,
            lastCorrelationCompletedAt: new DateTime(2026, 4, 5, 6, 7, 8, DateTimeKind.Utc),
            lastSubFlowStateChangedAt: new DateTime(2026, 4, 5, 6, 7, 7, DateTimeKind.Utc));
        var baseline = sut.ComputeEtag(input, fingerprint);

        // A new sub item started.
        sut.ComputeEtag(input, fingerprint with { CorrelationCount = 3 }).ShouldNotBe(baseline);
        // A sub item terminated (or a completion was reverted) without changing the total.
        sut.ComputeEtag(input, fingerprint with { CompletedCorrelationCount = 2 }).ShouldNotBe(baseline);
        // Revert-then-recomplete leaves both counts as they were; the timestamp separates them.
        sut.ComputeEtag(input, fingerprint with
        {
            LastCorrelationCompletedAt = new DateTime(2026, 4, 5, 6, 7, 9, DateTimeKind.Utc)
        }).ShouldNotBe(baseline);
        // A sub item advanced its own state — no count moves.
        sut.ComputeEtag(input, fingerprint with
        {
            LastSubFlowStateChangedAt = new DateTime(2026, 4, 5, 6, 7, 10, DateTimeKind.Utc)
        }).ShouldNotBe(baseline);
    }

    /// <summary>
    /// The counterpart guarantee: an unchanged correlation set must reproduce the same ETag, so a quiet
    /// instance keeps answering 304 instead of re-sending the body on every poll.
    /// </summary>
    [Fact]
    public void ComputeEtag_IsStableForAnUnchangedCorrelationSet()
    {
        var sut = CreateSut();
        var input = CreateInput();
        var completedAt = new DateTime(2026, 4, 5, 6, 7, 8, DateTimeKind.Utc);

        var first = sut.ComputeEtag(input, CreateFingerprint(
            correlationCount: 2, completedCorrelationCount: 1, lastCorrelationCompletedAt: completedAt));
        var second = sut.ComputeEtag(input, CreateFingerprint(
            correlationCount: 2, completedCorrelationCount: 1, lastCorrelationCompletedAt: completedAt));

        second.ShouldBe(first);
    }

    /// <summary>
    /// Counts must not collapse into one another in the hash material: 2 total / 1 completed must never
    /// hash the same as 1 total / 2 completed (a naive concatenation would let "21" collide with "12").
    /// </summary>
    [Fact]
    public void ComputeEtag_DoesNotConflateCorrelationCounts()
    {
        var sut = CreateSut();
        var input = CreateInput();

        var twoOfOne = sut.ComputeEtag(input,
            CreateFingerprint(correlationCount: 21, completedCorrelationCount: 1));
        var oneOfTwo = sut.ComputeEtag(input,
            CreateFingerprint(correlationCount: 2, completedCorrelationCount: 11));

        oneOfTwo.ShouldNotBe(twoOfOne);
    }

    [Fact]
    public void ComputeEtag_ChangesWithCallerScope()
    {
        var sut = CreateSut();
        var fingerprint = CreateFingerprint();

        _currentUser.ActorUserName.Returns("alice");
        var aliceEtag = sut.ComputeEtag(CreateInput(), fingerprint);

        _currentUser.ActorUserName.Returns("bob");
        var bobEtag = sut.ComputeEtag(CreateInput(), fingerprint);

        aliceEtag.ShouldNotBe(bobEtag);
    }

    [Fact]
    public void ComputeEtag_SubFlowOverload_FoldsDisplayedStateAndStatusIntoHash()
    {
        var sut = CreateSut();
        var input = CreateInput();
        var fingerprint = CreateFingerprint();
        var plain = sut.ComputeEtag(input, fingerprint);

        var subFlowActive = sut.ComputeEtag(input, fingerprint, new GetInstanceStateOutput
        {
            State = "sub-review",
            Status = InstanceStatus.Active
        });
        var subFlowBusy = sut.ComputeEtag(input, fingerprint, new GetInstanceStateOutput
        {
            State = "sub-review",
            Status = InstanceStatus.Busy
        });

        // Subflow Busy/Active flips within the same subflow state must produce distinct ETags,
        // and the subflow variant must never collide with the plain fingerprint ETag.
        subFlowActive.ShouldNotBe(plain);
        subFlowBusy.ShouldNotBe(subFlowActive);
    }

    private static InstanceStateFingerprint CreateFingerprint(
        int correlationCount = 0,
        int completedCorrelationCount = 0,
        DateTime? lastCorrelationCompletedAt = null,
        DateTime? lastSubFlowStateChangedAt = null) =>
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "test-key", "review",
            InstanceStatus.Active, "1.0.0", HasActiveSubFlow: false,
            CorrelationCount: correlationCount,
            CompletedCorrelationCount: completedCorrelationCount,
            LastCorrelationCompletedAt: lastCorrelationCompletedAt,
            LastSubFlowStateChangedAt: lastSubFlowStateChangedAt);
}
