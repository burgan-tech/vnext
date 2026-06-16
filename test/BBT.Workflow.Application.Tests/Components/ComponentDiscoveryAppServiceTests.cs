using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Caching;
using BBT.Workflow.Components;
using BBT.Workflow.Definitions;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;
using DefWorkflow = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Application.Tests.Components;

/// <summary>
/// Unit tests for <see cref="ComponentDiscoveryAppService"/>: domain-scoped listing with paging,
/// version dispatch to the component cache store, and mapping code decoding.
/// </summary>
public sealed class ComponentDiscoveryAppServiceTests : IDisposable
{
    private const string Domain = "test-domain";

    private readonly IRuntimeService _runtimeService = Substitute.For<IRuntimeService>();
    private readonly IComponentCacheStore _cacheStore = Substitute.For<IComponentCacheStore>();
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

        var runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
        _service = new ComponentDiscoveryAppService(ambient, runtimeInfo, _runtimeService, _cacheStore);
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
    public async Task ListAsync_filters_by_domain_and_paginates()
    {
        var workflows = new[]
        {
            CreateWorkflow("flow-a", Domain),
            CreateWorkflow("flow-b", Domain),
            CreateWorkflow("flow-c", Domain),
            CreateWorkflow("other", "different-domain")
        };
        _runtimeService.GetAsync<DefWorkflow>(Arg.Any<CancellationToken>())
            .Returns(workflows.AsEnumerable());

        var result = await _service.ListAsync(Domain, ComponentType.Workflows, page: 1, pageSize: 2);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.TotalCount.ShouldBe(3); // 3 in domain, 1 filtered out
        result.Value.Items.Count.ShouldBe(2); // page size
        result.Value.Items.ShouldAllBe(i => i.Domain == Domain);
        result.Value.Items.ShouldAllBe(i => i.Type == "workflows");
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

    private static DefWorkflow CreateWorkflow(string key, string domain)
    {
        var workflow = DefWorkflow.Create();
        Set(workflow, nameof(DefWorkflow.Key), key);
        Set(workflow, nameof(DefWorkflow.Domain), domain);
        Set(workflow, nameof(DefWorkflow.Version), "1.0.0");
        return workflow;
    }

    private static void Set(object target, string property, object value)
    {
        var prop = target.GetType().GetProperty(property,
            BindingFlags.Public | BindingFlags.Instance);
        prop!.SetValue(target, value);
    }

    public void Dispose() => AmbientServiceProvider.Current = _previousAmbient;
}
