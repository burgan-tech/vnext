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
        Substitute.For<IComponentL1Cache>());

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
}
