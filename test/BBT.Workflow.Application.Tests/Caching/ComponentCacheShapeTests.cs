using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using FlowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Caching;

/// <summary>
/// The component cache keys carry a shape segment (<see cref="ComponentCacheShape.Version"/>), so a
/// runtime never reads an entry serialized by a runtime whose entity type had a different set of members.
/// </summary>
/// <remarks>
/// The bug these pin: a pre-row-security runtime re-wrote the shared Redis entry of a flow declaring
/// <c>rowSecurity</c>; its serializer had no such member, so the entry carried none. The row-security
/// runtime read that entry, saw <see cref="FlowDefinition.RowSecurity"/> == null and served every row.
/// </remarks>
public sealed class ComponentCacheShapeTests
{
    private const string Domain = "core";
    private const string FlowKey = "rs-a";
    private const string FullVersion = "1.0.0-pkg.1.0.0+core";
    private const string ComponentType = "sys-flows";

    private const string WorkflowJson = """
        {
            "type": "F",
            "labels": [{"label": "Test", "language": "en"}],
            "states": [
                {
                    "key": "review",
                    "stateType": 2,
                    "versionStrategy": "Minor",
                    "labels": [{"label": "Review", "language": "en"}],
                    "transitions": []
                }
            ],
            "sharedTransitions": [],
            "startTransition": {
                "key": "start",
                "target": "review",
                "versionStrategy": "Minor",
                "triggerType": 0,
                "labels": [{"label": "Start", "language": "en"}]
            }
            __ROW_SECURITY__
        }
        """;

    private const string RowSecurityMember = """
        ,"rowSecurity": {
          "rules": [ { "roles": ["branch-officer"], "filter": {"attributes":{"branchCode":{"eq":"$.context.Headers.x-branch"}}} } ]
        }
        """;

    private static FlowDefinition CreateWorkflow(bool withRowSecurity)
    {
        var json = WorkflowJson.Replace("__ROW_SECURITY__", withRowSecurity ? RowSecurityMember : "");
        var workflow = JsonSerializer.Deserialize<FlowDefinition>(json, JsonSerializerConstants.JsonOptions)!;
        workflow.SetReference(new Reference(FlowKey, Domain, ComponentType, FullVersion));
        return workflow;
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            var time = new CacheSetTestHarness.AdjustableTimeProvider(
                new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
            var options = Options.Create(new ComponentCacheOptions());
            var guids = Substitute.For<IGuidGenerator>();
            guids.Create().Returns(_ => Guid.NewGuid());

            Cache = new FakeDistributedCacheService(time, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Backend = Substitute.For<ICacheBackend<FlowDefinition>>();
            Backend.LoadAllByKeyAsync(Domain, FlowKey, Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(Result<List<FlowDefinition>>.Ok([CreateWorkflow(withRowSecurity: true)])));
            Backend.LoadAsync(Domain, FlowKey, Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(Result<FlowDefinition>.Ok(CreateWorkflow(withRowSecurity: true))));

            Generations = new ComponentGenerationProvider(
                Cache, guids, options, time, NullLogger<ComponentGenerationProvider>.Instance);

            Sut = new CacheSet<FlowDefinition>(
                Cache,
                Backend,
                Generations,
                options,
                time,
                NullLogger<CacheSet<FlowDefinition>>.Instance,
                new ComponentL1Cache(options, time, NullLogger<ComponentL1Cache>.Instance));
        }

        public FakeDistributedCacheService Cache { get; }
        public ICacheBackend<FlowDefinition> Backend { get; }
        public ComponentGenerationProvider Generations { get; }
        public CacheSet<FlowDefinition> Sut { get; }

        /// <summary>Writes an entry the way a runtime without the shape segment (and without rowSecurity) did.</summary>
        public Task WriteLegacyAsync(string key)
            => Cache.SetAsync(key, new CacheEnvelope<FlowDefinition>
            {
                Domain = Domain,
                Key = FlowKey,
                Version = FullVersion,
                Flow = ComponentType,
                Entity = CreateWorkflow(withRowSecurity: false),
            });
    }

    [Fact]
    public void ShapeVersion_IsANonEmptySegment()
    {
        ComponentCacheShape.Version.ShouldNotBeNullOrWhiteSpace();
        ComponentCacheShape.Version.ShouldNotContain(":");
    }

    [Fact]
    public async Task FullAndResolutionKeys_CarryTheShapeSegment()
    {
        var fixture = new Fixture();

        (await fixture.Sut.GetLatestByNameAsync(Domain, FlowKey)).IsSuccess.ShouldBeTrue();
        (await fixture.Sut.GetByVersionAsync(Domain, FlowKey, FullVersion)).IsSuccess.ShouldBeTrue();

        var generation = await fixture.Generations.GetAsync(ComponentType, Domain, FlowKey);
        var shape = ComponentCacheShape.Version;
        fixture.Cache.Keys.ShouldContain($"{ComponentType}:{Domain}:{FlowKey}:{shape}:full:1.0.0-pkg.1.0.0");
        fixture.Cache.Keys.ShouldContain($"{ComponentType}:{Domain}:{FlowKey}:{shape}:res:{generation}:latest");
        fixture.Cache.Keys
            .Where(k => k.Contains(":full:") || k.Contains(":res:"))
            .ShouldAllBe(k => k.StartsWith($"{ComponentType}:{Domain}:{FlowKey}:{shape}:"));
    }

    [Fact]
    public async Task LegacyResolutionEntryWithoutRowSecurity_IsNotRead_ResolveReloadsFromBackend()
    {
        // Arrange - a pre-feature pod wrote "latest" under the shared generation, without the shape segment.
        var fixture = new Fixture();
        var generation = await fixture.Generations.GetAsync(ComponentType, Domain, FlowKey);
        await fixture.WriteLegacyAsync($"{ComponentType}:{Domain}:{FlowKey}:res:{generation}:latest");

        // Act
        var result = await fixture.Sut.GetLatestByNameAsync(Domain, FlowKey);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value!.RowSecurity.ShouldNotBeNull();
        await fixture.Backend.Received(1).LoadAllByKeyAsync(Domain, FlowKey, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LegacyFullEntryWithoutRowSecurity_IsNotRead_GetReloadsFromBackend()
    {
        // Arrange - a pre-feature pod wrote the exact version without the shape segment.
        var fixture = new Fixture();
        await fixture.WriteLegacyAsync($"{ComponentType}:{Domain}:{FlowKey}:full:1.0.0-pkg.1.0.0");

        // Act
        var result = await fixture.Sut.GetByVersionAsync(Domain, FlowKey, FullVersion);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value!.RowSecurity.ShouldNotBeNull();
        await fixture.Backend.Received(1).LoadAsync(Domain, FlowKey, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
