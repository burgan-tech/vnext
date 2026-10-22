using System;
using BBT.Workflow.Definitions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Caching;

/// <summary>
/// Unit tests for <see cref="DomainCacheContext"/>'s component-type-key dispatch.
/// </summary>
/// <remarks>
/// The string overload exists so a caller holding only a component type key — for example
/// <c>DefinitionAppService.InvalidateCacheAsync</c>, which receives the flow as a string — can invalidate
/// without knowing the entity type. If the mapping is wrong the caller silently invalidates nothing, so
/// the keys are pinned here rather than assumed.
/// </remarks>
public class DomainCacheContextTests
{
    private readonly DomainCacheContext _context = new(
        Substitute.For<BBT.Aether.DistributedCache.IDistributedCacheService>(),
        Substitute.For<ICacheBackend<Definitions.Workflow>>(),
        Substitute.For<ICacheBackend<WorkflowTask>>(),
        Substitute.For<ICacheBackend<SchemaDefinition>>(),
        Substitute.For<ICacheBackend<Function>>(),
        Substitute.For<ICacheBackend<View>>(),
        Substitute.For<ICacheBackend<Extension>>(),
        Substitute.For<ICacheBackend<Mapping>>(),
        Substitute.For<IComponentGenerationProvider>(),
        Options.Create(new ComponentCacheOptions()),
        TimeProvider.System,
        NullLoggerFactory.Instance,
        Substitute.For<IComponentL1Cache>(),
        Substitute.For<IVersionPolicySource>());

    [Fact]
    public void Set_ByComponentTypeKey_ShouldResolveEveryCachedComponentType()
    {
        _context.Set(Definitions.Workflow.ComponentTypeKey).ShouldBeSameAs(_context.Workflows);
        _context.Set(WorkflowTask.ComponentTypeKey).ShouldBeSameAs(_context.Tasks);
        _context.Set(SchemaDefinition.ComponentTypeKey).ShouldBeSameAs(_context.Schemas);
        _context.Set(Function.ComponentTypeKey).ShouldBeSameAs(_context.Functions);
        _context.Set(View.ComponentTypeKey).ShouldBeSameAs(_context.Views);
        _context.Set(Extension.ComponentTypeKey).ShouldBeSameAs(_context.Extensions);
        _context.Set(Mapping.ComponentTypeKey).ShouldBeSameAs(_context.Mappings);
    }

    [Fact]
    public void Set_ByComponentTypeKey_ShouldMatchTheFlowNamesCallersActuallyPass()
    {
        // The cast processor and DefinitionAppService both pass the flow name, so these literals are the
        // real contract rather than the ComponentTypeKey constants restated.
        _context.Set("sys-views").ShouldBeSameAs(_context.Views);
        _context.Set("sys-flows").ShouldBeSameAs(_context.Workflows);
        _context.Set("sys-tasks").ShouldBeSameAs(_context.Tasks);
        _context.Set("sys-schemas").ShouldBeSameAs(_context.Schemas);
        _context.Set("sys-functions").ShouldBeSameAs(_context.Functions);
        _context.Set("sys-extensions").ShouldBeSameAs(_context.Extensions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-component-type")]
    public void Set_ByUnknownComponentTypeKey_ShouldReturnNull(string componentTypeKey)
    {
        _context.Set(componentTypeKey).ShouldBeNull();
    }

    [Fact]
    public void Set_ByComponentTypeKey_ShouldIgnoreCase()
    {
        _context.Set("SYS-VIEWS").ShouldBeSameAs(_context.Views);
    }

    private static Definitions.Workflow Flow(string key)
    {
        var flow = Definitions.Workflow.Create();
        flow.SetReference(new Reference(key, "core", "sys-flows", "1.0.0"));
        return flow;
    }

    private static WorkflowTask TaskItem(string key)
    {
        var task = System.Text.Json.JsonSerializer.Deserialize<WorkflowTask>(
            """{"type":"6","config":{"url":"https://example.com","method":"GET","timeoutSeconds":30,"validateSSL":false}}""",
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        task.SetReference(new Reference(key, "core", "sys-tasks", "1.0.0"));
        return task;
    }

    [Fact]
    public async System.Threading.Tasks.Task DomainCacheContext_ShouldWireRegistryPolicyOnlyForWorkflows()
    {
        var policy = Substitute.For<IVersionPolicySource>();
        policy.GetDefaultVersionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(BBT.Aether.Results.Result<string?>.Ok("1.0.0"));
        var workflowBackend = Substitute.For<ICacheBackend<Definitions.Workflow>>();
        workflowBackend.LoadAllByKeyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(BBT.Aether.Results.Result<System.Collections.Generic.List<Definitions.Workflow>>.Ok([Flow("some-flow")]));
        var taskBackend = Substitute.For<ICacheBackend<WorkflowTask>>();
        taskBackend.LoadAllByKeyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(BBT.Aether.Results.Result<System.Collections.Generic.List<WorkflowTask>>.Ok([TaskItem("some-task")]));

        var cache = new FakeDistributedCacheService(TimeProvider.System);
        var optionsAccessor = Options.Create(new ComponentCacheOptions());
        var context = new DomainCacheContext(
            cache,
            workflowBackend,
            taskBackend,
            Substitute.For<ICacheBackend<SchemaDefinition>>(),
            Substitute.For<ICacheBackend<Function>>(),
            Substitute.For<ICacheBackend<View>>(),
            Substitute.For<ICacheBackend<Extension>>(),
            Substitute.For<ICacheBackend<Mapping>>(),
            new ComponentGenerationProvider(
                cache,
                new BBT.Aether.Guids.SequentialGuidGenerator(),
                optionsAccessor,
                TimeProvider.System,
                NullLogger<ComponentGenerationProvider>.Instance),
            optionsAccessor,
            TimeProvider.System,
            NullLoggerFactory.Instance,
            new ComponentL1Cache(optionsAccessor, TimeProvider.System, NullLogger<ComponentL1Cache>.Instance),
            policy);

        await context.Workflows.GetByVersionAsync("core", "some-flow", null);
        await context.Tasks.GetByVersionAsync("core", "some-task", null);

        await policy.Received(1).GetDefaultVersionAsync("core", "some-flow", Arg.Any<System.Threading.CancellationToken>());
        await policy.DidNotReceive().GetDefaultVersionAsync("core", "some-task", Arg.Any<System.Threading.CancellationToken>());
    }
}
