using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedCache;
using BBT.Aether.Users;
using BBT.Workflow.Instances.DTOs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// Unit tests for <see cref="InstanceSchemaFunctionCache"/>: master/schema key and ETag
/// composition (state sensitivity only on schema, transition-key scoping, caller scoping
/// without extensions), TTL resolution, failure-degrades-to-miss, and entry round-trip.
/// </summary>
public class InstanceSchemaFunctionCacheTests
{
    private const string TestDomain = "test-domain";
    private const string TestWorkflow = "test-flow";
    private const string TestInstance = "instance-1";

    private readonly IDistributedCacheService _distributedCache = Substitute.For<IDistributedCacheService>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    private InstanceSchemaFunctionCache CreateSut(InstanceFunctionCacheOptions? options = null) =>
        new(_distributedCache,
            _currentUser,
            Options.Create(options ?? new InstanceFunctionCacheOptions()),
            Substitute.For<ILogger<InstanceSchemaFunctionCache>>());

    private static GetMasterInput CreateMasterInput(
        IReadOnlyList<string>? roles = null,
        string? version = null,
        Dictionary<string, string?>? headers = null) => new()
    {
        Domain = TestDomain,
        Workflow = TestWorkflow,
        Instance = TestInstance,
        Roles = roles,
        Version = version,
        Headers = headers ?? new Dictionary<string, string?>()
    };

    private static GetSchemaInput CreateSchemaInput(
        IReadOnlyList<string>? roles = null,
        string? version = null,
        Dictionary<string, string?>? headers = null) => new()
    {
        Domain = TestDomain,
        Workflow = TestWorkflow,
        Instance = TestInstance,
        Roles = roles,
        Version = version,
        Headers = headers ?? new Dictionary<string, string?>()
    };

    private static InstanceDataFingerprint CreateFingerprint() =>
        new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "test-key",
            "01JD2G4YV0EXAMPLEULID0000A", "1.0.0", "review", HasActiveSubFlow: false);

    [Fact]
    public void BuildKey_UsesDistinctPrefixesForMasterAndSchema()
    {
        var sut = CreateSut();

        sut.BuildKey(CreateMasterInput())
            .ShouldStartWith($"master-fn:v1:{TestDomain}:{TestWorkflow}:{TestInstance}:");
        sut.BuildKey(CreateSchemaInput(), "approve")
            .ShouldStartWith($"schema-fn:v1:{TestDomain}:{TestWorkflow}:{TestInstance}:");
        sut.BuildKey(CreateSchemaInput(), "approve").ShouldEndWith(":approve");
    }

    [Fact]
    public void SchemaBuildKey_DiffersByTransitionKey()
    {
        var sut = CreateSut();

        sut.BuildKey(CreateSchemaInput(), "approve")
            .ShouldNotBe(sut.BuildKey(CreateSchemaInput(), "reject"));
    }

    [Fact]
    public void MasterEtag_IgnoresEffectiveState_SchemaEtagDoesNot()
    {
        var sut = CreateSut();
        var fingerprint = CreateFingerprint();
        var moved = fingerprint with { EffectiveState = "approved" };

        sut.ComputeEtag(CreateMasterInput(), fingerprint)
            .ShouldBe(sut.ComputeEtag(CreateMasterInput(), moved));
        sut.ComputeEtag(CreateSchemaInput(), fingerprint, "approve")
            .ShouldNotBe(sut.ComputeEtag(CreateSchemaInput(), moved, "approve"));
    }

    [Fact]
    public void BothEtags_ChangeWithDataEtagAndFlowVersion()
    {
        var sut = CreateSut();
        var fingerprint = CreateFingerprint();

        var masterBaseline = sut.ComputeEtag(CreateMasterInput(), fingerprint);
        sut.ComputeEtag(CreateMasterInput(), fingerprint with { LatestDataEtag = "01JD2G4YV0OTHERULID000000" })
            .ShouldNotBe(masterBaseline);
        sut.ComputeEtag(CreateMasterInput(), fingerprint with { FlowVersion = "2.0.0" })
            .ShouldNotBe(masterBaseline);

        var schemaBaseline = sut.ComputeEtag(CreateSchemaInput(), fingerprint, "approve");
        sut.ComputeEtag(CreateSchemaInput(), fingerprint with { LatestDataEtag = "01JD2G4YV0OTHERULID000000" }, "approve")
            .ShouldNotBe(schemaBaseline);
        sut.ComputeEtag(CreateSchemaInput(), fingerprint with { FlowVersion = "2.0.0" }, "approve")
            .ShouldNotBe(schemaBaseline);
        sut.ComputeEtag(CreateSchemaInput(), fingerprint, "reject")
            .ShouldNotBe(schemaBaseline);
    }

    [Fact]
    public void Etags_ChangeWithCallerScope()
    {
        var sut = CreateSut();
        var fingerprint = CreateFingerprint();

        _currentUser.ActorUserName.Returns("alice");
        var alice = sut.ComputeEtag(CreateMasterInput(), fingerprint);

        _currentUser.ActorUserName.Returns("bob");
        var bob = sut.ComputeEtag(CreateMasterInput(), fingerprint);
        alice.ShouldNotBe(bob);

        sut.ComputeEtag(CreateMasterInput(roles: ["admin"]), fingerprint).ShouldNotBe(bob);
        sut.ComputeEtag(CreateMasterInput(version: "2.0.0"), fingerprint).ShouldNotBe(bob);
        sut.ComputeEtag(CreateMasterInput(headers: new Dictionary<string, string?>
        {
            ["Accept-Language"] = "tr-TR"
        }), fingerprint).ShouldNotBe(bob);
    }

    [Fact]
    public void ResolveTtlSeconds_PrefersWorkflowAuthorValue()
    {
        var sut = CreateSut(new InstanceFunctionCacheOptions { DefaultTtlSeconds = 60 });

        sut.ResolveTtlSeconds(FunctionCacheFromJson("""{ "ttlSeconds": 120 }""")).ShouldBe(120);
        sut.ResolveTtlSeconds(FunctionCacheFromJson("""{ "ttlSeconds": 0 }""")).ShouldBe(60);
        sut.ResolveTtlSeconds(null).ShouldBe(60);
    }

    [Fact]
    public async Task GetAsync_WhenCacheThrows_ReturnsNull()
    {
        _distributedCache
            .GetAsync<SchemaFunctionCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis down"));

        (await CreateSut().GetAsync("master-fn:some-key", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task SetAsync_WhenCacheThrows_DoesNotThrow()
    {
        _distributedCache
            .SetAsync(Arg.Any<string>(), Arg.Any<SchemaFunctionCacheEntry>(),
                Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("redis down"));

        await Should.NotThrowAsync(() =>
            CreateSut().SetAsync("schema-fn:some-key", new SchemaFunctionCacheEntry(), 60, CancellationToken.None));
    }

    [Fact]
    public async Task SetAsync_UsesGivenTtl()
    {
        DistributedCacheEntryOptions? captured = null;
        _distributedCache
            .SetAsync(Arg.Any<string>(), Arg.Any<SchemaFunctionCacheEntry>(),
                Arg.Do<DistributedCacheEntryOptions>(o => captured = o), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var before = DateTimeOffset.UtcNow;
        await CreateSut().SetAsync("master-fn:some-key", new SchemaFunctionCacheEntry(), 120, CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        captured.ShouldNotBeNull();
        captured!.AbsoluteExpiration!.Value.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(119));
        captured.AbsoluteExpiration!.Value.ShouldBeLessThanOrEqualTo(after.AddSeconds(121));
    }

    [Fact]
    public void CacheEntry_JsonRoundTrip_PreservesSchemaDocument()
    {
        using var doc = JsonDocument.Parse("""{ "type": "object", "properties": { "name": {} } }""");
        var entry = new SchemaFunctionCacheEntry
        {
            Etag = "etag-1",
            Output = new GetSchemaOutput
            {
                Key = "master-schema",
                Type = "Json",
                Schema = doc.RootElement.Clone()
            }
        };

        var json = JsonSerializer.Serialize(entry);
        var roundTripped = JsonSerializer.Deserialize<SchemaFunctionCacheEntry>(json);

        roundTripped.ShouldNotBeNull();
        roundTripped!.Etag.ShouldBe("etag-1");
        roundTripped.Output.Key.ShouldBe("master-schema");
        roundTripped.Output.Schema.GetProperty("type").GetString().ShouldBe("object");
    }

    private static Definitions.FunctionCacheDefinition FunctionCacheFromJson(string json) =>
        JsonSerializer.Deserialize<Definitions.FunctionCacheDefinition>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;
}
