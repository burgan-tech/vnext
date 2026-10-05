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
/// Unit tests for <see cref="DataFunctionCache"/>: cache-key/ETag composition (caller scoping,
/// data-etag and flow-version sensitivity), workflow-author TTL resolution, failure-degrades-
/// to-miss behavior, and envelope JSON round-trip including the JsonElement data payload.
/// </summary>
public class DataFunctionCacheTests
{
    private const string TestDomain = "test-domain";
    private const string TestWorkflow = "test-flow";
    private const string TestInstance = "instance-1";

    private readonly IDistributedCacheService _distributedCache = Substitute.For<IDistributedCacheService>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    private DataFunctionCache CreateSut(
        InstanceFunctionCacheOptions? options = null, bool maskingEnabled = true,
        BBT.Workflow.Authorization.SchemaEncryptionOptions? encryption = null) =>
        new(_distributedCache,
            _currentUser,
            Options.Create(options ?? new InstanceFunctionCacheOptions()),
            Substitute.For<ILogger<DataFunctionCache>>(),
            Options.Create(new BBT.Workflow.Authorization.SchemaMaskingOptions { Enabled = maskingEnabled }),
            Options.Create(encryption ?? new BBT.Workflow.Authorization.SchemaEncryptionOptions()));

    private static GetInstanceDataInput CreateInput(
        IReadOnlyList<string>? roles = null,
        string[]? extensions = null,
        string? version = null,
        Dictionary<string, string?>? headers = null) => new()
    {
        Domain = TestDomain,
        Workflow = TestWorkflow,
        Instance = TestInstance,
        Roles = roles,
        Extensions = extensions,
        Version = version,
        Headers = headers ?? new Dictionary<string, string?>(),
        QueryParameters = new Dictionary<string, string?>()
    };

    private static InstanceDataFingerprint CreateFingerprint() =>
        new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "test-key",
            "01JD2G4YV0EXAMPLEULID0000A", "1.0.0", "review", HasActiveSubFlow: false);

    [Fact]
    public void Options_Defaults_AreEnabledWith60SecondDefaultTtl()
    {
        var options = new InstanceFunctionCacheOptions();

        options.Enabled.ShouldBeTrue();
        options.DefaultTtlSeconds.ShouldBe(60);
    }

    [Fact]
    public void BuildKey_ContainsDomainWorkflowAndInstance()
    {
        var key = CreateSut().BuildKey(CreateInput());

        // The v2 segment is a cache generation: bump it whenever a change alters what a cached body
        // means for a given caller hash — v1 for provider-resolved caller roles, v2 for x-masking, v3 for x-encryption encrypt.
        key.ShouldStartWith($"data-fn:v3:{TestDomain}:{TestWorkflow}:{TestInstance}:");
    }

    [Fact]
    public void ComputeEtag_IsDeterministicAndRolesOrderInsensitive()
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
        sut.ComputeEtag(input, fingerprint with { LatestDataEtag = "01JD2G4YV0OTHERULID000000" }).ShouldNotBe(baseline);
        sut.ComputeEtag(input, fingerprint with { FlowVersion = "2.0.0" }).ShouldNotBe(baseline);
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

        var withCulture = sut.ComputeEtag(CreateInput(headers: new Dictionary<string, string?>
        {
            ["Accept-Language"] = "tr-TR"
        }), fingerprint);
        var withVersion = sut.ComputeEtag(CreateInput(version: "1.0.0"), fingerprint);

        withCulture.ShouldNotBe(bobEtag);
        withVersion.ShouldNotBe(bobEtag);
    }

    /// <summary>
    /// Extensions are outside the caller scope: the ETag tracks the data change point and the
    /// key is shared across extension variants (the body cache only serves extensionless
    /// requests; extension demand always rebuilds fresh).
    /// </summary>
    [Fact]
    public void ComputeEtagAndBuildKey_IgnoreRequestedExtensions()
    {
        var sut = CreateSut();
        var fingerprint = CreateFingerprint();

        sut.ComputeEtag(CreateInput(extensions: ["ext-a"]), fingerprint)
            .ShouldBe(sut.ComputeEtag(CreateInput(), fingerprint));
        sut.BuildKey(CreateInput(extensions: ["ext-a"]))
            .ShouldBe(sut.BuildKey(CreateInput()));
    }

    [Fact]
    public void ResolveTtlSeconds_PrefersWorkflowAuthorValue()
    {
        var sut = CreateSut(new InstanceFunctionCacheOptions { DefaultTtlSeconds = 60 });

        sut.ResolveTtlSeconds(FunctionCacheFromJson("""{ "ttlSeconds": 120 }""")).ShouldBe(120);
        sut.ResolveTtlSeconds(FunctionCacheFromJson("""{ "ttlSeconds": null }""")).ShouldBe(60);
        sut.ResolveTtlSeconds(FunctionCacheFromJson("""{ "ttlSeconds": 0 }""")).ShouldBe(60);
        sut.ResolveTtlSeconds(FunctionCacheFromJson("""{ "ttlSeconds": -5 }""")).ShouldBe(60);
        sut.ResolveTtlSeconds(null).ShouldBe(60);
    }

    [Fact]
    public void Workflow_JsonBinding_ReadsFunctionCacheDefinition()
    {
        var json = """
                   {
                       "type": "F",
                       "config": { "functionCache": { "ttlSeconds": 120 } },
                       "labels": [], "functions": [], "features": [], "states": [],
                       "sharedTransitions": [], "extensions": [],
                       "startTransition": {"key": "start", "from": null, "target": "review", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
                   }
                   """;
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(json, options);

        workflow.ShouldNotBeNull();
        workflow!.Config.ShouldNotBeNull();
        workflow.Config!.FunctionCache.ShouldNotBeNull();
        workflow.Config.FunctionCache!.TtlSeconds.ShouldBe(120);
    }

    [Fact]
    public async Task GetAsync_WhenCacheThrows_ReturnsNull()
    {
        _distributedCache
            .GetAsync<DataFunctionCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var entry = await CreateSut().GetAsync("some-key", CancellationToken.None);

        entry.ShouldBeNull();
    }

    [Fact]
    public async Task SetAsync_WhenCacheThrows_DoesNotThrow()
    {
        _distributedCache
            .SetAsync(Arg.Any<string>(), Arg.Any<DataFunctionCacheEntry>(),
                Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis down"));

        await Should.NotThrowAsync(() =>
            CreateSut().SetAsync("some-key", new DataFunctionCacheEntry(), 60, CancellationToken.None));
    }

    [Fact]
    public async Task SetAsync_UsesGivenTtl()
    {
        DistributedCacheEntryOptions? captured = null;
        _distributedCache
            .SetAsync(Arg.Any<string>(), Arg.Any<DataFunctionCacheEntry>(),
                Arg.Do<DistributedCacheEntryOptions>(o => captured = o), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var before = DateTimeOffset.UtcNow;
        await CreateSut().SetAsync("some-key", new DataFunctionCacheEntry(), 120, CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        captured.ShouldNotBeNull();
        captured!.AbsoluteExpiration.ShouldNotBeNull();
        captured.AbsoluteExpiration!.Value.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(119));
        captured.AbsoluteExpiration!.Value.ShouldBeLessThanOrEqualTo(after.AddSeconds(121));
    }

    [Fact]
    public void CacheEntry_JsonRoundTrip_PreservesData()
    {
        using var doc = JsonDocument.Parse("""{ "name": "Ada", "age": 36 }""");
        var entry = new DataFunctionCacheEntry
        {
            Etag = "etag-1",
            EntityEtag = "entity-1",
            Data = doc.RootElement.Clone()
        };

        var json = JsonSerializer.Serialize(entry);
        var roundTripped = JsonSerializer.Deserialize<DataFunctionCacheEntry>(json);

        roundTripped.ShouldNotBeNull();
        roundTripped!.Etag.ShouldBe("etag-1");
        roundTripped.EntityEtag.ShouldBe("entity-1");
        roundTripped.Data.ShouldNotBeNull();
        roundTripped.Data!.Value.GetProperty("name").GetString().ShouldBe("Ada");
    }

    private static Definitions.FunctionCacheDefinition FunctionCacheFromJson(string json) =>
        JsonSerializer.Deserialize<Definitions.FunctionCacheDefinition>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;

    [Fact]
    public void ComputeEtag_ContainsTheGeneration_SoAPreMaskingEtagNeverValidates()
    {
        // Recompute the v1 material by hand: id | latestDataEtag | flowVersion | callerHash. A client that
        // cached a body under v1 must get a 200, never a 304, from the fingerprint fast path.
        var sut = CreateSut();
        var input = CreateInput();
        var fingerprint = CreateFingerprint();
        var v2 = sut.ComputeEtag(input, fingerprint);
        var callerHash = sut.BuildKey(input).Split(':')[^1];
        var v1Material = string.Join('|', fingerprint.Id, fingerprint.LatestDataEtag, fingerprint.FlowVersion, callerHash);
        var v1 = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(v1Material)))[..32];

        v2.ShouldNotBe(v1);
    }

    [Fact]
    public void MaskingSwitch_MovesBothTheKeyAndTheEtag()
    {
        var input = CreateInput();
        var fingerprint = CreateFingerprint();
        var on = CreateSut(maskingEnabled: true);
        var off = CreateSut(maskingEnabled: false);

        off.BuildKey(input).ShouldNotBe(on.BuildKey(input));
        off.ComputeEtag(input, fingerprint).ShouldNotBe(on.ComputeEtag(input, fingerprint));
        off.BuildKey(input).ShouldStartWith("data-fn:v3-nomask:");
    }
}
