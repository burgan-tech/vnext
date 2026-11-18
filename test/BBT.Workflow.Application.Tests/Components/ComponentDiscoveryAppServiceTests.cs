using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Caching;
using BBT.Workflow.Components;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;
using DefWorkflow = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Application.Tests.Components;

/// <summary>
/// Unit tests for <see cref="ComponentDiscoveryAppService"/>: domain-scoped listing with paging from version
/// metadata, the version list with its pointer and latest resolution, version dispatch to the component cache
/// store, and mapping code decoding.
/// </summary>
public sealed class ComponentDiscoveryAppServiceTests : IDisposable
{
    private const string Domain = "test-domain";
    private const string FlowKey = "contract-approval-workflow";

    private static readonly DateTime PublishedAt = new(2026, 11, 9, 9, 28, 0, DateTimeKind.Utc);

    /// <summary>The package builds of the issue #1192 report, oldest first.</summary>
    private static readonly string[] Builds =
    [
        "1.0.0-pkg.1.0.345+contract",
        "1.0.0-pkg.1.0.349+contract",
        "1.0.0-pkg.1.0.350+contract"
    ];

    /// <summary>The same builds newest publish first, published 349, 350, then 345 (an older build last).</summary>
    private static readonly string[] NewestPublishFirst =
    [
        "1.0.0-pkg.1.0.345+contract",
        "1.0.0-pkg.1.0.350+contract",
        "1.0.0-pkg.1.0.349+contract"
    ];

    private readonly IRuntimeService _runtimeService = Substitute.For<IRuntimeService>();
    private readonly IComponentCacheStore _cacheStore = Substitute.For<IComponentCacheStore>();
    private readonly IRuntimeInfoProvider _runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
    private readonly CacheSetTestHarness.FakeVersionPolicySource _policy = new();
    private readonly IDomainCacheContext _cacheContext = Substitute.For<IDomainCacheContext>();
    private readonly ICacheSet<DefWorkflow> _workflowSet = Substitute.For<ICacheSet<DefWorkflow>>();
    private readonly ICacheSet<View> _viewSet = Substitute.For<ICacheSet<View>>();
    private readonly ComponentDiscoveryAppService _service;
    private readonly IServiceProvider? _previousAmbient;

    public ComponentDiscoveryAppServiceTests()
    {
        // Ambient provider needed by PostSharp UnitOfWork/aspect interception on ApplicationService.
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IUnitOfWorkManager>());
        var ambient = services.BuildServiceProvider();
        _previousAmbient = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = ambient;

        // As wired today: the workflow set applies the pointer to every key but system ones, the others never.
        _cacheContext.Set<DefWorkflow>().Returns(_workflowSet);
        _cacheContext.Set<View>().Returns(_viewSet);
        _workflowSet.AppliesVersionPolicy(Arg.Is<string>(k => !k.StartsWith("sys-"))).Returns(true);

        _service = new ComponentDiscoveryAppService(
            ambient, _runtimeInfo, _runtimeService, _cacheStore, _cacheContext, _policy);
    }

    [Theory]
    [InlineData("workflows", true)]
    [InlineData("Mappings", true)]
    [InlineData("SCHEMAS", true)]
    [InlineData("bogus", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryParse_handles_known_and_unknown_tokens(string? token, bool expected)
    {
        ComponentTypeExtensions.TryParse(token, out _).ShouldBe(expected);
    }

    [Fact]
    public void ToComponentTypeKey_maps_each_type_to_sys_schema()
    {
        ComponentType.Workflows.ToComponentTypeKey().ShouldBe(RuntimeSysSchemaInfo.Flows);
        ComponentType.Mappings.ToComponentTypeKey().ShouldBe(RuntimeSysSchemaInfo.Mappings);
        ComponentType.Schemas.ToComponentTypeKey().ShouldBe(RuntimeSysSchemaInfo.Schemas);
    }

    [Fact]
    public async Task ListAsync_lists_version_metadata_read_as_the_requested_domain_and_paginates()
    {
        _runtimeService.GetPublishedVersionsAsync<DefWorkflow>(Arg.Any<CancellationToken>())
            .Returns([
                new PublishedComponentVersion("flow-a", "1.0.0-pkg.1.0.2+core", PublishedAt),
                new PublishedComponentVersion("flow-a", "1.0.0-pkg.1.0.1+core", PublishedAt.AddDays(-1)),
                new PublishedComponentVersion("flow-b", "2.0.0-pkg.1.0.1+core", PublishedAt)
            ]);

        var result = await _service.ListAsync(Domain, ComponentType.Workflows, page: 1, pageSize: 2);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.TotalCount.ShouldBe(3); // one summary per published version
        result.Value.Items.Count.ShouldBe(2); // page size
        result.Value.Items.ShouldAllBe(i => i.Domain == Domain && i.Type == "workflows" && i.ComponentTypeKey == RuntimeSysSchemaInfo.Flows);
        result.Value.Items[0].Key.ShouldBe("flow-a");
        result.Value.Items[0].Version.ShouldBe("1.0.0-pkg.1.0.2+core");
        result.Value.Items[0].CreatedAt.ShouldBe(PublishedAt, "the summary carries when the version was published");
        _runtimeInfo.Received().UseDomain(Domain);
    }

    [Fact]
    public async Task GetVersionsAsync_without_a_pointer_resolves_latest_to_the_highest_package_build()
    {
        Published(Builds);
        Page(skip: 0, take: 21, NewestPublishFirst);

        var result = await _service.GetVersionsAsync(Domain, ComponentType.Workflows, FlowKey, page: 1, pageSize: 20);

        result.IsSuccess.ShouldBeTrue();
        var versions = result.Value!;
        versions.Key.ShouldBe(FlowKey);
        versions.ComponentTypeKey.ShouldBe(RuntimeSysSchemaInfo.Flows);
        versions.TotalVersions.ShouldBe(3);
        versions.DefaultVersion.ShouldBeNull();
        versions.Latest.Version.ShouldBe("1.0.0-pkg.1.0.350+contract");
        versions.Latest.Outcome.ShouldBe("resolved");
        versions.Latest.Source.ShouldBe("automatic");
        versions.Items.Select(i => i.Version).ShouldBe(NewestPublishFirst, "the query's publish order is kept");
        versions.Items.Single(i => i.LatestRow).Version.ShouldBe("1.0.0-pkg.1.0.350+contract",
            "the store's latest flag is mapped as the query reports it");
        versions.HasNext.ShouldBeFalse();
        _runtimeInfo.Received().UseDomain(Domain);
    }

    [Fact]
    public async Task GetVersionsAsync_with_a_pointer_reports_it_and_resolves_latest_to_the_pointer()
    {
        Published(Builds);
        Page(skip: 0, take: 21, NewestPublishFirst);
        _policy.Pointer = "1.0.0-pkg.1.0.349";

        var result = await _service.GetVersionsAsync(Domain, ComponentType.Workflows, FlowKey, page: 1, pageSize: 20);

        result.Value!.DefaultVersion.ShouldBe("1.0.0-pkg.1.0.349");
        result.Value.Latest.Version.ShouldBe("1.0.0-pkg.1.0.349+contract");
        result.Value.Latest.Source.ShouldBe("default");
    }

    [Fact]
    public async Task GetVersionsAsync_with_a_pointer_matching_nothing_reports_pointerInvalid()
    {
        Published(Builds);
        Page(skip: 0, take: 21, Builds);
        _policy.Pointer = "2.0";

        var result = await _service.GetVersionsAsync(Domain, ComponentType.Workflows, FlowKey, page: 1, pageSize: 20);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Latest.Version.ShouldBeNull();
        result.Value.Latest.Outcome.ShouldBe("pointerInvalid");
    }

    [Fact]
    public async Task GetVersionsAsync_when_the_registry_cannot_be_read_fails_like_resolution_does()
    {
        Published(Builds);
        Page(skip: 0, take: 21, Builds);
        _policy.Fail = true;

        var result = await _service.GetVersionsAsync(Domain, ComponentType.Workflows, FlowKey, page: 1, pageSize: 20);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("Test:RegistryUnavailable");
    }

    [Fact]
    public async Task GetVersionsAsync_with_a_malformed_pointer_fails_with_the_409_resolution_gives()
    {
        Published(Builds);
        Page(skip: 0, take: 21, Builds);
        _policy.Fail = true;
        _policy.FailureError = WorkflowErrors.FlowDefaultVersionInvalid(Domain, FlowKey, " ");

        var result = await _service.GetVersionsAsync(Domain, ComponentType.Workflows, FlowKey, page: 1, pageSize: 20);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FlowDefaultVersionInvalid);
    }

    [Fact]
    public async Task GetVersionsAsync_asks_the_components_cache_set_whether_a_pointer_applies()
    {
        _runtimeService.GetVersionsAsync<View>("account-view", Arg.Any<CancellationToken>())
            .Returns(["1.0.0-pkg.1.0.1+core", "1.1.0-pkg.1.0.1+core"]);
        _viewSet.AppliesVersionPolicy("account-view").Returns(true);
        _policy.Pointer = "1.0";

        var result = await _service.GetVersionsAsync(Domain, ComponentType.Views, "account-view", page: 1, pageSize: 20);

        result.Value!.DefaultVersion.ShouldBe("1.0", "a set wired with the policy has its pointer read and applied");
        result.Value.Latest.Version.ShouldBe("1.0.0-pkg.1.0.1+core");
        result.Value.Latest.Source.ShouldBe("default");
        _policy.Calls.ShouldBe(1);
    }

    [Theory]
    [InlineData(ComponentType.Views, "account-view")]
    [InlineData(ComponentType.Workflows, "sys-flows")]
    public async Task GetVersionsAsync_reads_no_pointer_where_the_policy_does_not_apply(ComponentType type, string key)
    {
        _runtimeService.GetVersionsAsync<View>(key, Arg.Any<CancellationToken>()).Returns(["1.0.0-pkg.1.0.1+core"]);
        _runtimeService.GetVersionsAsync<DefWorkflow>(key, Arg.Any<CancellationToken>()).Returns(["1.0.0-pkg.1.0.1+core"]);
        _policy.Pointer = "0.9";

        var result = await _service.GetVersionsAsync(Domain, type, key, page: 1, pageSize: 20);

        result.Value!.DefaultVersion.ShouldBeNull();
        result.Value.Latest.Version.ShouldBe("1.0.0-pkg.1.0.1+core");
        _policy.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task GetVersionsAsync_for_a_component_with_no_published_version_is_not_found()
    {
        Published();

        var result = await _service.GetVersionsAsync(Domain, ComponentType.Workflows, FlowKey, page: 1, pageSize: 20);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.CacheItemNotFound);
        await _runtimeService.DidNotReceiveWithAnyArgs().GetVersionPageAsync<DefWorkflow>(default!, default, default);
    }

    [Fact]
    public async Task GetVersionsAsync_pages_by_one_row_past_the_page()
    {
        Published(Builds);
        Page(skip: 2, take: 3, "1.0.0-pkg.1.0.2+contract", "1.0.0-pkg.1.0.1+contract", "1.0.0-pkg.1.0.0+contract");

        var result = await _service.GetVersionsAsync(Domain, ComponentType.Workflows, FlowKey, page: 2, pageSize: 2);

        result.Value!.Page.ShouldBe(2);
        result.Value.PageSize.ShouldBe(2);
        result.Value.Items.Count.ShouldBe(2);
        result.Value.HasNext.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(500, 100)]
    public async Task GetVersionsAsync_bounds_the_page_size(int requested, int used)
    {
        Published(Builds);
        Page(skip: 0, take: used + 1, Builds);

        var result = await _service.GetVersionsAsync(Domain, ComponentType.Workflows, FlowKey, page: 1, pageSize: requested);

        result.Value!.PageSize.ShouldBe(used);
        await _runtimeService.Received(1).GetVersionPageAsync<DefWorkflow>(FlowKey, 0, used + 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAsync_propagates_cache_store_failure()
    {
        _cacheStore.GetFlowAsync(Domain, "missing", null, Arg.Any<CancellationToken>())
            .Returns(Result<DefWorkflow>.Fail(Error.NotFound("notfound", "nope")));

        var result = await _service.GetAsync(Domain, ComponentType.Workflows, "missing", null);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("notfound");
    }

    [Fact]
    public async Task GetMappingCodeAsync_returns_decoded_code_and_encoding()
    {
        var mapping = new Mapping("helper", "var x = 1;", CodeEncoding.Native);
        _cacheStore.GetMappingAsync(Domain, "helper", null, Arg.Any<CancellationToken>())
            .Returns(Result<Mapping>.Ok(mapping));

        var result = await _service.GetMappingCodeAsync(Domain, "helper", null);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Code.ShouldBe("var x = 1;");
        result.Value.Encoding.ShouldBe("Native");
    }

    private void Published(params string[] versions)
        => _runtimeService.GetVersionsAsync<DefWorkflow>(FlowKey, Arg.Any<CancellationToken>())
            .Returns(versions.ToList());

    /// <summary>
    /// The page the version list query answers, newest publish first. The store's latest flag sits on the
    /// highest build (<c>…350</c>) wherever it is in publish order.
    /// </summary>
    private void Page(int skip, int take, params string[] newestFirst)
        => _runtimeService.GetVersionPageAsync<DefWorkflow>(FlowKey, skip, take, Arg.Any<CancellationToken>())
            .Returns(newestFirst
                .Select((v, i) => new ComponentVersionSummary(
                    v, v.Contains("1.0.350", StringComparison.Ordinal), "1.0.0", PublishedAt.AddMinutes(-i)))
                .ToList());

    public void Dispose() => AmbientServiceProvider.Current = _previousAmbient;
}
